using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace WisperFlow;

/// <summary>
/// Overlay window showing recording/transcribing status.
/// Positioned at the top-center of the primary screen.
/// Does NOT steal focus from other windows.
/// </summary>
public partial class OverlayWindow : Window
{
    private Storyboard? _pulseAnimation;
    private Storyboard? _spinAnimation;
    private System.Timers.Timer? _errorHideTimer;

    // Win32 constants for non-activating window
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public OverlayWindow()
    {
        InitializeComponent();
        
        // Position at top-center of primary screen
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2;
        Top = 50;

        // Get animation storyboards
        _pulseAnimation = (Storyboard)Resources["PulseAnimation"];
        _spinAnimation = (Storyboard)Resources["SpinAnimation"];

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
    /// Shows the recording state with pulsing red dot.
    /// </summary>
    public void ShowRecording(string? mode = null)
    {
        Dispatcher.Invoke(() =>
        {
            HideAllPanels();
            RecordingPanel.Visibility = Visibility.Visible;
            RecordingTime.Text = mode ?? "";
            
            _pulseAnimation?.Begin();
            ShowNoActivate();
        });
    }

    /// <summary>
    /// Updates the recording time display.
    /// </summary>
    public void UpdateRecordingTime(TimeSpan duration)
    {
        Dispatcher.Invoke(() =>
        {
            RecordingTime.Text = duration.ToString(@"m\:ss");
        });
    }

    /// <summary>
    /// Shows the transcribing state with spinning indicator.
    /// </summary>
    public void ShowTranscribing(string? message = null)
    {
        Dispatcher.Invoke(() =>
        {
            HideAllPanels();
            TranscribingPanel.Visibility = Visibility.Visible;
            TranscribingText.Text = message ?? "Transcribing...";
            
            _pulseAnimation?.Stop();
            _spinAnimation?.Begin();
            ShowNoActivate();
        });
    }

    /// <summary>
    /// Shows the polishing state.
    /// </summary>
    public void ShowPolishing(string? message = null)
    {
        Dispatcher.Invoke(() =>
        {
            TranscribingText.Text = message ?? "Polishing...";
        });
    }

    /// <summary>
    /// Shows an error message for a few seconds.
    /// </summary>
    public void ShowError(string message)
    {
        Dispatcher.Invoke(() =>
        {
            HideAllPanels();
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = message;
            
            _pulseAnimation?.Stop();
            _spinAnimation?.Stop();
            ShowNoActivate();

            // Auto-hide after 3 seconds
            _errorHideTimer?.Dispose();
            _errorHideTimer = new System.Timers.Timer(3000);
            _errorHideTimer.Elapsed += (s, e) =>
            {
                _errorHideTimer?.Dispose();
                _errorHideTimer = null;
                Dispatcher.Invoke(Hide);
            };
            _errorHideTimer.AutoReset = false;
            _errorHideTimer.Start();
        });
    }

    /// <summary>
    /// Shows the window without stealing focus.
    /// </summary>
    private void ShowNoActivate()
    {
        // Show without activating
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
    /// Hides the overlay.
    /// </summary>
    public new void Hide()
    {
        Dispatcher.Invoke(() =>
        {
            _pulseAnimation?.Stop();
            _spinAnimation?.Stop();
            _errorHideTimer?.Dispose();
            _errorHideTimer = null;
            
            base.Hide();
        });
    }

    private void HideAllPanels()
    {
        RecordingPanel.Visibility = Visibility.Collapsed;
        TranscribingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Prevent closing, just hide
        e.Cancel = true;
        Hide();
    }
}
