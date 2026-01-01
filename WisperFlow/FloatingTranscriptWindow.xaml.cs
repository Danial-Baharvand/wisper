using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WisperFlow;

/// <summary>
/// Floating window that shows real-time transcript preview during dictation.
/// Features:
/// - Appears with fade-in when first transcript segment arrives
/// - Shows text with animated cursor (like typing)
/// - Matrix-style scramble effect when processing
/// - Fades out when ready to inject
/// </summary>
public partial class FloatingTranscriptWindow : Window
{
    private Storyboard? _fadeInAnimation;
    private Storyboard? _fadeOutAnimation;
    private Storyboard? _cursorBlinkAnimation;
    
    private DispatcherTimer? _matrixTimer;
    private string _committedText = "";   // Text from final results (locked in)
    private string _currentInterim = "";  // Current interim text (will be replaced)
    private string _targetText = "";
    private int _matrixIterations;
    private Random _random = new();
    
    private bool _isVisible;
    private bool _isMatrixAnimating;
    
    // Matrix-style characters (mix of alphanumeric and symbols)
    private const string MatrixChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@#$%&*+=<>?";
    
    // Win32 constants for non-activating window
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public FloatingTranscriptWindow()
    {
        InitializeComponent();
        
        // Position below the main overlay (which is at Top=50)
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2;
        Top = 140; // Below the recording overlay
        
        // Get animations
        _fadeInAnimation = (Storyboard)Resources["FadeInAnimation"];
        _fadeOutAnimation = (Storyboard)Resources["FadeOutAnimation"];
        _cursorBlinkAnimation = (Storyboard)Resources["CursorBlinkAnimation"];
        
        // Hook fade-out completed to hide window
        if (_fadeOutAnimation != null)
        {
            _fadeOutAnimation.Completed += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    base.Hide();
                    _isVisible = false;
                    TranscriptText.Text = "";
                    _committedText = "";
                    _currentInterim = "";
                });
            };
        }
        
        // Make window non-activating after it's created
        SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
        
        // Start hidden
        Hide();
    }
    
    /// <summary>
    /// Updates the transcript with interim or final results.
    /// Interim results replace the current interim; final results commit to the transcript.
    /// </summary>
    public void UpdateTranscript(string text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        
        Dispatcher.Invoke(() =>
        {
            if (isFinal)
            {
                // Final result: commit to the transcript
                if (!string.IsNullOrEmpty(_committedText) && !_committedText.EndsWith(" "))
                {
                    _committedText += " ";
                }
                _committedText += text.Trim();
                _currentInterim = "";  // Clear interim since it's now committed
                
                TranscriptText.Text = _committedText;
            }
            else
            {
                // Interim result: show committed text + current interim (which replaces previous interim)
                _currentInterim = text.Trim();
                
                var displayText = _committedText;
                if (!string.IsNullOrEmpty(displayText) && !displayText.EndsWith(" "))
                {
                    displayText += " ";
                }
                displayText += _currentInterim;
                
                TranscriptText.Text = displayText;
            }
            
            // Auto-scroll to end
            TranscriptScroller.ScrollToEnd();
            
            // Show window if not visible
            if (!_isVisible)
            {
                ShowWithFadeIn();
            }
            
            // Update status based on interim/final
            StatusText.Text = isFinal ? "Ready" : "Listening...";
            LiveDot.Fill = new System.Windows.Media.SolidColorBrush(
                isFinal 
                    ? System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda)  // Bright cyan for final
                    : System.Windows.Media.Color.FromRgb(0x00, 0xd4, 0xaa)); // Green for interim
        });
    }
    
    /// <summary>
    /// Legacy method - calls UpdateTranscript with isFinal=true.
    /// </summary>
    public void AppendTranscript(string text)
    {
        UpdateTranscript(text, isFinal: true);
    }
    
    /// <summary>
    /// Updates status to show utterance processing complete.
    /// </summary>
    public void ShowUtteranceComplete()
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = "Ready";
            LiveDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda)); // Bright cyan
        });
    }
    
    /// <summary>
    /// Starts the Matrix scramble effect (called when hotkey is released).
    /// The text will scramble randomly before the window fades out.
    /// </summary>
    public void StartMatrixEffect()
    {
        var displayText = _committedText + (_currentInterim.Length > 0 ? " " + _currentInterim : "");
        if (_isMatrixAnimating || string.IsNullOrEmpty(displayText.Trim()))
            return;
        
        Dispatcher.Invoke(() =>
        {
            _isMatrixAnimating = true;
            _targetText = displayText.Trim();
            _matrixIterations = 0;
            
            // Stop cursor animation
            _cursorBlinkAnimation?.Stop();
            TypingCursor.Visibility = Visibility.Collapsed;
            
            // Update status
            StatusText.Text = "Processing...";
            LiveDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x00, 0xd4, 0xaa)); // Green
            
            // Start matrix scramble timer
            _matrixTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(40) // Fast scramble
            };
            _matrixTimer.Tick += MatrixTimer_Tick;
            _matrixTimer.Start();
        });
    }
    
    private void MatrixTimer_Tick(object? sender, EventArgs e)
    {
        _matrixIterations++;
        
        // Scramble effect: replace random characters with Matrix characters
        var chars = _targetText.ToCharArray();
        int scrambleCount = Math.Max(1, chars.Length / 3); // Scramble ~1/3 of characters per tick
        
        for (int i = 0; i < scrambleCount; i++)
        {
            int pos = _random.Next(chars.Length);
            if (!char.IsWhiteSpace(chars[pos]))
            {
                chars[pos] = MatrixChars[_random.Next(MatrixChars.Length)];
            }
        }
        
        TranscriptText.Text = new string(chars);
        
        // Gradually change color to green
        var greenIntensity = Math.Min(255, 100 + _matrixIterations * 15);
        TranscriptText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0, (byte)greenIntensity, (byte)(greenIntensity / 2)));
        
        // After 15 iterations (~600ms), fade out
        if (_matrixIterations >= 15)
        {
            _matrixTimer?.Stop();
            _matrixTimer = null;
            _isMatrixAnimating = false;
            
            // Fade out
            HideWithFadeOut();
        }
    }
    
    /// <summary>
    /// Immediately hides without animation (for cancellation).
    /// </summary>
    public void HideImmediate()
    {
        Dispatcher.Invoke(() =>
        {
            _matrixTimer?.Stop();
            _matrixTimer = null;
            _isMatrixAnimating = false;
            _cursorBlinkAnimation?.Stop();
            _fadeInAnimation?.Stop();
            _fadeOutAnimation?.Stop();
            
            base.Hide();
            _isVisible = false;
            TranscriptText.Text = "";
            _committedText = "";
            _currentInterim = "";
            
            // Reset text color
            TranscriptText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xe6, 0xf1, 0xff));
        });
    }
    
    /// <summary>
    /// Shows the window with a fade-in animation.
    /// </summary>
    private void ShowWithFadeIn()
    {
        ShowNoActivate();
        _isVisible = true;
        
        TypingCursor.Visibility = Visibility.Visible;
        _cursorBlinkAnimation?.Begin();
        _fadeInAnimation?.Begin();
    }
    
    /// <summary>
    /// Hides the window with a fade-out animation.
    /// </summary>
    private void HideWithFadeOut()
    {
        _fadeOutAnimation?.Begin();
    }
    
    /// <summary>
    /// Shows the window without stealing focus.
    /// </summary>
    private void ShowNoActivate()
    {
        base.Show();
        
        // Ensure we don't have focus
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((extendedStyle & WS_EX_NOACTIVATE) == 0)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            }
        }
    }
    
    /// <summary>
    /// Hides the window (stops animations first).
    /// </summary>
    public new void Hide()
    {
        Dispatcher.Invoke(() =>
        {
            _matrixTimer?.Stop();
            _matrixTimer = null;
            _isMatrixAnimating = false;
            _cursorBlinkAnimation?.Stop();
            
            if (_isVisible)
            {
                HideWithFadeOut();
            }
        });
    }
    
    /// <summary>
    /// Resets the window for a new dictation session.
    /// </summary>
    public void Reset()
    {
        Dispatcher.Invoke(() =>
        {
            _matrixTimer?.Stop();
            _matrixTimer = null;
            _isMatrixAnimating = false;
            _committedText = "";
            _currentInterim = "";
            TranscriptText.Text = "";
            StatusText.Text = "Listening...";
            
            // Reset text color
            TranscriptText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xe6, 0xf1, 0xff));
            
            TypingCursor.Visibility = Visibility.Visible;
        });
    }
    
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Prevent closing, just hide
        e.Cancel = true;
        HideImmediate();
    }
}
