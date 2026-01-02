using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WisperFlow;

/// <summary>
/// Unified dictation bar that sits at the bottom of the screen.
/// Handles all states: idle, hover, recording, speaking, transcribing.
/// Voice bars respond to actual audio intensity.
/// </summary>
public partial class DictationBar : Window
{
    public enum BarState
    {
        Idle,           // Small bar with subtle animation
        Hovered,        // Expanded with hint text
        Recording,      // Recording but no speech detected
        Speaking,       // Speech detected, bolder animation
        Processing,     // Transcribing/polishing
        Error           // Error state
    }
    
    private BarState _currentState = BarState.Idle;
    private string _hotkeyDisplayName = "caps";
    
    // Animations
    private Storyboard? _hintExpandAnimation;
    private Storyboard? _hintCollapseAnimation;
    private Storyboard? _transcriptExpandAnimation;
    private Storyboard? _transcriptCollapseAnimation;
    private Storyboard? _mainBarExpandAnimation;
    private Storyboard? _mainBarContractAnimation;
    private Storyboard? _warningFadeInAnimation;
    private Storyboard? _warningFadeOutAnimation;
    
    // Warning bar timers
    private DispatcherTimer? _warningHideTimer;
    private DispatcherTimer? _warningFadeCompleteTimer;  // Timer that fires after fade-out animation completes
    
    // Voice bars
    private Rectangle[] _bars = null!;
    private double[] _barTargetHeights = null!;
    private double[] _barCurrentHeights = null!;
    private double[] _barVelocities = null!;
    private DispatcherTimer? _animationTimer;
    private Random _random = new();
    
    // Audio level (0.0 to 1.0)
    private double _currentAudioLevel = 0.0;
    private double _peakAudioLevel = 0.0;
    private DateTime _lastPeakTime = DateTime.MinValue;
    
    // Cursor blink timer
    private DispatcherTimer? _cursorBlinkTimer;
    private bool _cursorVisible = true;
    
    // Transcript state
    private string _committedText = "";
    private string _currentInterim = "";
    private bool _transcriptVisible = false;
    
    // Floating browser state
    private FloatingBrowserWindow? _floatingBrowser;
    private string _selectedProvider = "ChatGPT";
    private bool _providerButtonsVisible = false;
    private Storyboard? _providerButtonsShowAnimation;
    private Storyboard? _providerButtonsHideAnimation;
    
    // Left side buttons (settings, screenshot)
    private bool _leftButtonsVisible = false;
    private Storyboard? _leftButtonsShowAnimation;
    private Storyboard? _leftButtonsHideAnimation;
    
    // Screenshot context state
    private bool _screenshotEnabled = false;
    
    /// <summary>
    /// Gets or sets the currently selected AI provider (ChatGPT or Gemini).
    /// </summary>
    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (_selectedProvider != value)
            {
                _selectedProvider = value;
                UpdateProviderButtonSelection();
                SelectedProviderChanged?.Invoke(this, value);
            }
        }
    }
    
    /// <summary>
    /// Event fired when the selected provider changes.
    /// </summary>
    public event EventHandler<string>? SelectedProviderChanged;
    
    /// <summary>
    /// Gets or sets whether screenshot context is enabled.
    /// </summary>
    public bool ScreenshotEnabled
    {
        get => _screenshotEnabled;
        set
        {
            if (_screenshotEnabled != value)
            {
                _screenshotEnabled = value;
                UpdateScreenshotButtonState();
                ScreenshotEnabledChanged?.Invoke(this, value);
            }
        }
    }
    
    /// <summary>
    /// Event fired when screenshot context enabled state changes.
    /// </summary>
    public event EventHandler<bool>? ScreenshotEnabledChanged;
    
    /// <summary>
    /// Event fired when settings button is clicked.
    /// </summary>
    public event EventHandler? SettingsRequested;
    
    /// <summary>
    /// Gets the floating browser window instance.
    /// </summary>
    public FloatingBrowserWindow? FloatingBrowser => _floatingBrowser;
    
    
    // Win32 constants
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public DictationBar()
    {
        InitializeComponent();
        
        // Position at bottom-center of screen, just above taskbar
        PositionAtBottom();
        
        // Get animations
        _hintExpandAnimation = (Storyboard)Resources["HintExpandAnimation"];
        _hintCollapseAnimation = (Storyboard)Resources["HintCollapseAnimation"];
        _transcriptExpandAnimation = (Storyboard)Resources["TranscriptExpandAnimation"];
        _transcriptCollapseAnimation = (Storyboard)Resources["TranscriptCollapseAnimation"];
        _mainBarExpandAnimation = (Storyboard)Resources["MainBarExpandAnimation"];
        _mainBarContractAnimation = (Storyboard)Resources["MainBarContractAnimation"];
        _warningFadeInAnimation = (Storyboard)Resources["WarningFadeInAnimation"];
        _warningFadeOutAnimation = (Storyboard)Resources["WarningFadeOutAnimation"];
        _providerButtonsShowAnimation = (Storyboard)Resources["ProviderButtonsShowAnimation"];
        _providerButtonsHideAnimation = (Storyboard)Resources["ProviderButtonsHideAnimation"];
        _leftButtonsShowAnimation = (Storyboard)Resources["LeftButtonsShowAnimation"];
        _leftButtonsHideAnimation = (Storyboard)Resources["LeftButtonsHideAnimation"];
        
        // Initialize bars array
        _bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8, Bar9 };
        _barTargetHeights = new double[_bars.Length];
        _barCurrentHeights = new double[_bars.Length];
        _barVelocities = new double[_bars.Length];
        
        for (int i = 0; i < _bars.Length; i++)
        {
            _barTargetHeights[i] = 4;
            _barCurrentHeights[i] = 4;
            _barVelocities[i] = 0;
        }
        
        // Set up animation timer (60fps for smooth animation)
        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animationTimer.Tick += AnimationTimer_Tick;
        _animationTimer.Start();
        
        // Set up cursor blink timer
        _cursorBlinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530)
        };
        _cursorBlinkTimer.Tick += CursorBlinkTimer_Tick;
        
        // Mouse events for hover
        MainBar.MouseEnter += MainBar_MouseEnter;
        MainBar.MouseLeave += MainBar_MouseLeave;
        
        // Make window non-activating
        SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
        
        // Start visible
        Show();
    }
    
    private void PositionAtBottom()
    {
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2;
        Top = screen.Height - Height;
    }
    
    /// <summary>
    /// Sets the hotkey display text (e.g., "caps", "F1", "ctrl+shift+d").
    /// </summary>
    public void SetHotkey(string hotkeyName)
    {
        _hotkeyDisplayName = hotkeyName.ToLower();
        Dispatcher.Invoke(() =>
        {
            HotkeyText.Text = _hotkeyDisplayName;
        });
    }
    
    #region Audio Level Animation
    
    /// <summary>
    /// Updates the audio level for voice bar animation.
    /// Call this from audio recording callback.
    /// </summary>
    public void UpdateAudioLevel(float level)
    {
        _currentAudioLevel = Math.Clamp(level, 0, 1);
        
        // Track peak with decay
        if (_currentAudioLevel > _peakAudioLevel)
        {
            _peakAudioLevel = _currentAudioLevel;
            _lastPeakTime = DateTime.Now;
        }
        else if ((DateTime.Now - _lastPeakTime).TotalMilliseconds > 100)
        {
            _peakAudioLevel *= 0.95; // Decay peak
        }
    }
    
    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        // Calculate target heights based on state and audio level
        UpdateBarTargets();
        
        // Animate bars with spring physics for natural movement
        for (int i = 0; i < _bars.Length; i++)
        {
            // Spring physics
            double springStrength = 0.3;
            double damping = 0.7;
            
            double displacement = _barTargetHeights[i] - _barCurrentHeights[i];
            _barVelocities[i] += displacement * springStrength;
            _barVelocities[i] *= damping;
            _barCurrentHeights[i] += _barVelocities[i];
            
            // Clamp
            _barCurrentHeights[i] = Math.Max(3, Math.Min(28, _barCurrentHeights[i]));
            
            // Apply to UI
            _bars[i].Height = _barCurrentHeights[i];
        }
    }
    
    private void UpdateBarTargets()
    {
        double baseHeight;
        double variation;
        double audioInfluence;
        
        switch (_currentState)
        {
            case BarState.Idle:
            case BarState.Hovered:
                // Subtle idle animation - gentle wave
                baseHeight = 5;
                variation = 2;
                audioInfluence = 0;
                
                double time = DateTime.Now.Ticks / 10000000.0; // seconds
                for (int i = 0; i < _bars.Length; i++)
                {
                    // Different frequencies for each bar for organic movement
                    double phase = i * 0.7;
                    double wave = Math.Sin(time * 1.5 + phase) * 0.5 + 0.5;
                    _barTargetHeights[i] = baseHeight + wave * variation;
                }
                break;
                
            case BarState.Recording:
            case BarState.Speaking:
                // Audio-driven animation - primarily responds to voice
                // Increased sensitivity: normal speaking should show visible animation
                baseHeight = 4;
                double recTime = DateTime.Now.Ticks / 10000000.0;
                
                for (int i = 0; i < _bars.Length; i++)
                {
                    // Center bars react more strongly
                    double centerWeight = 1.0 - Math.Abs(i - 4) / 5.0;
                    centerWeight = 0.5 + centerWeight * 0.5; // Range 0.5 to 1.0
                    
                    // Audio is the PRIMARY driver - HIGH sensitivity for normal speaking
                    double audioHeight = _currentAudioLevel * 50 * centerWeight;
                    double peakHeight = _peakAudioLevel * 15 * centerWeight;
                    
                    // Small amount of organic movement (not dominant)
                    double phase = i * 0.5;
                    double subtleWave = Math.Sin(recTime * 3.0 + phase) * 2;
                    
                    // Add per-bar randomness for natural feel
                    double noise = (_random.NextDouble() - 0.5) * (2 + _currentAudioLevel * 8);
                    
                    _barTargetHeights[i] = baseHeight + audioHeight + peakHeight + subtleWave + noise;
                    
                    // Minimum height when any audio is present (lowered threshold)
                    if (_currentAudioLevel > 0.02)
                    {
                        _barTargetHeights[i] = Math.Max(_barTargetHeights[i], 6);
                    }
                }
                break;
                
            case BarState.Processing:
                // Processing - calm pulsing
                baseHeight = 7;
                variation = 3;
                
                double procTime = DateTime.Now.Ticks / 10000000.0;
                for (int i = 0; i < _bars.Length; i++)
                {
                    double phase = i * 0.3;
                    double wave = Math.Sin(procTime * 2.0 + phase) * 0.5 + 0.5;
                    _barTargetHeights[i] = baseHeight + wave * variation;
                }
                break;
                
            case BarState.Error:
                // Error - slow pulse
                baseHeight = 6;
                variation = 2;
                
                double errTime = DateTime.Now.Ticks / 10000000.0;
                double errorWave = Math.Sin(errTime * 1.0) * 0.5 + 0.5;
                for (int i = 0; i < _bars.Length; i++)
                {
                    _barTargetHeights[i] = baseHeight + errorWave * variation;
                }
                break;
        }
    }
    
    #endregion
    
    #region Cursor Position
    
    // Cursor is now inline with text via InlineUIContainer, no positioning needed
    
    #endregion
    
    #region Mouse Events
    
    private void MainBar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_currentState == BarState.Idle)
        {
            TransitionToState(BarState.Hovered);
        }
        ShowProviderButtons();
        ShowLeftButtons();
    }
    
    private void MainBar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_currentState == BarState.Hovered)
        {
            TransitionToState(BarState.Idle);
        }
        // Keep provider buttons visible if browser is open or mouse is over them
        if (_floatingBrowser?.IsVisible != true && !IsMouseOverProviderButtons())
        {
            HideProviderButtons();
        }
        // Keep left buttons visible if mouse is over them
        if (!IsMouseOverLeftButtons())
        {
            HideLeftButtons();
        }
    }
    
    private bool IsMouseOverProviderButtons()
    {
        var mousePos = Mouse.GetPosition(ProviderButtonsPanel);
        return mousePos.X >= 0 && mousePos.X <= ProviderButtonsPanel.ActualWidth &&
               mousePos.Y >= 0 && mousePos.Y <= ProviderButtonsPanel.ActualHeight;
    }
    
    private bool IsMouseOverLeftButtons()
    {
        var mousePos = Mouse.GetPosition(LeftButtonsPanel);
        return mousePos.X >= 0 && mousePos.X <= LeftButtonsPanel.ActualWidth &&
               mousePos.Y >= 0 && mousePos.Y <= LeftButtonsPanel.ActualHeight;
    }
    
    #endregion
    
    #region Provider Buttons and Floating Browser
    
    /// <summary>
    /// Shows the AI provider selection buttons with animation.
    /// </summary>
    public void ShowProviderButtons()
    {
        if (_providerButtonsVisible) return;
        _providerButtonsVisible = true;
        
        ProviderButtonsPanel.Visibility = Visibility.Visible;
        _providerButtonsShowAnimation?.Begin();
        UpdateProviderButtonSelection();
    }
    
    /// <summary>
    /// Hides the AI provider selection buttons with animation.
    /// </summary>
    public void HideProviderButtons()
    {
        if (!_providerButtonsVisible) return;
        if (_floatingBrowser?.IsVisible == true) return; // Keep visible if browser is open
        
        _providerButtonsVisible = false;
        _providerButtonsHideAnimation?.Begin();
    }
    
    /// <summary>
    /// Updates the visual selection state of provider buttons.
    /// </summary>
    private void UpdateProviderButtonSelection()
    {
        // ChatGPT button - filled when selected
        if (_selectedProvider == "ChatGPT")
        {
            ChatGPTIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 163, 127));
            GeminiIndicator.Fill = new SolidColorBrush(Color.FromArgb(51, 66, 133, 244)); // 0.2 opacity
        }
        else
        {
            ChatGPTIndicator.Fill = new SolidColorBrush(Color.FromArgb(51, 16, 163, 127)); // 0.2 opacity
            GeminiIndicator.Fill = new SolidColorBrush(Color.FromRgb(66, 133, 244));
        }
    }
    
    /// <summary>
    /// ChatGPT button click handler.
    /// </summary>
    private async void ChatGPTButton_Click(object sender, MouseButtonEventArgs e)
    {
        await ToggleProvider("ChatGPT");
    }
    
    /// <summary>
    /// Gemini button click handler.
    /// </summary>
    private async void GeminiButton_Click(object sender, MouseButtonEventArgs e)
    {
        await ToggleProvider("Gemini");
    }
    
    /// <summary>
    /// Toggles the floating browser for the specified provider.
    /// If already open with same provider, closes it. Otherwise opens/switches.
    /// </summary>
    public async Task ToggleProvider(string provider)
    {
        // If clicking the already-selected provider and browser is open, close it
        if (_selectedProvider == provider && _floatingBrowser?.IsVisible == true)
        {
            await CloseFloatingBrowserAsync();
            return;
        }
        
        // Update selection
        SelectedProvider = provider;
        
        // Open or switch browser
        await OpenFloatingBrowserAsync(provider);
    }
    
    /// <summary>
    /// Opens the floating browser for the specified provider.
    /// </summary>
    public async Task OpenFloatingBrowserAsync(string provider)
    {
        // Create browser window if it doesn't exist
        if (_floatingBrowser == null)
        {
            _floatingBrowser = new FloatingBrowserWindow();
            _floatingBrowser.BrowserClosing += OnBrowserClosing;
            await _floatingBrowser.InitializeAsync(provider);
        }
        else if (_floatingBrowser.CurrentProvider != provider)
        {
            // Instant switch - just toggle visibility between cached WebViews
            await _floatingBrowser.SwitchProviderAsync(provider);
        }
        
        // Show if: not visible, OR in pre-init mode (window is off-screen), OR position is off-screen
        var needsShow = !_floatingBrowser.IsVisible || _floatingBrowser.IsPreInitializing || _floatingBrowser.Left < -1000;
        if (needsShow)
        {
            _floatingBrowser.ShowWithAnimation();
        }
        
        SelectedProvider = provider;
    }
    
    /// <summary>
    /// Opens the floating browser and submits a query.
    /// </summary>
    /// <param name="provider">The AI provider (ChatGPT or Gemini).</param>
    /// <param name="query">The query text to submit.</param>
    /// <param name="screenshotBytes">Optional screenshot to attach to the message.</param>
    public async Task OpenAndQueryAsync(string provider, string query, byte[]? screenshotBytes = null)
    {
        await OpenFloatingBrowserAsync(provider);
        
        if (_floatingBrowser != null)
        {
            await _floatingBrowser.NavigateAndQueryAsync(query, screenshotBytes);
        }
    }
    
    /// <summary>
    /// Closes the floating browser with animation.
    /// </summary>
    public async Task CloseFloatingBrowserAsync()
    {
        if (_floatingBrowser != null)
        {
            await _floatingBrowser.HideWithAnimationAsync();
        }
    }
    
    /// <summary>
    /// Pre-initializes all AI provider browsers in the background on app startup.
    /// Shows the window off-screen with zero opacity to trigger full rendering.
    /// This ensures instant response when the user first opens any provider.
    /// </summary>
    public async Task PreInitializeBrowsersAsync()
    {
        if (_floatingBrowser != null) return; // Already initialized
        
        _floatingBrowser = new FloatingBrowserWindow();
        _floatingBrowser.BrowserClosing += OnBrowserClosing;
        
        // Enable pre-init mode to prevent OnLoaded from repositioning
        _floatingBrowser.SetPreInitMode(true);
        
        // Position off-screen and make invisible to trigger rendering without user seeing it
        _floatingBrowser.Left = -9999;
        _floatingBrowser.Top = -9999;
        _floatingBrowser.Opacity = 0;
        _floatingBrowser.Show(); // Must show for WebView2 to render
        
        // Initialize both providers (this navigates and waits for page load)
        await _floatingBrowser.InitializeAsync(_selectedProvider);
        
        // Additional wait for JavaScript frameworks to fully initialize
        await Task.Delay(3000);
        
        // Hide the window and disable pre-init mode
        _floatingBrowser.Hide();
        _floatingBrowser.SetPreInitMode(false);
        _floatingBrowser.Opacity = 1; // Restore opacity for later
    }
    
    private void OnBrowserClosing(object? sender, EventArgs e)
    {
        // Optionally hide provider buttons when browser closes
        if (_currentState == BarState.Idle && !IsMouseOverProviderButtons())
        {
            HideProviderButtons();
        }
    }
    
    /// <summary>
    /// Sets the selected provider without opening the browser.
    /// Used when loading from settings.
    /// </summary>
    public void SetSelectedProvider(string provider)
    {
        _selectedProvider = provider;
        UpdateProviderButtonSelection();
    }
    
    #endregion
    
    #region Left Side Buttons (Settings, Screenshot)
    
    /// <summary>
    /// Shows the left side buttons (Settings, Screenshot) with animation.
    /// </summary>
    public void ShowLeftButtons()
    {
        if (_leftButtonsVisible) return;
        _leftButtonsVisible = true;
        
        LeftButtonsPanel.Visibility = Visibility.Visible;
        _leftButtonsShowAnimation?.Begin();
    }
    
    /// <summary>
    /// Hides the left side buttons with animation.
    /// </summary>
    public void HideLeftButtons()
    {
        if (!_leftButtonsVisible) return;
        
        _leftButtonsVisible = false;
        _leftButtonsHideAnimation?.Begin();
    }
    
    /// <summary>
    /// Settings button click handler.
    /// </summary>
    private void SettingsButton_Click(object sender, MouseButtonEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }
    
    /// <summary>
    /// Screenshot button click handler - toggles screenshot context.
    /// </summary>
    private void ScreenshotButton_Click(object sender, MouseButtonEventArgs e)
    {
        ScreenshotEnabled = !ScreenshotEnabled;
    }
    
    /// <summary>
    /// Updates the visual state of the screenshot button.
    /// </summary>
    private void UpdateScreenshotButtonState()
    {
        Dispatcher.Invoke(() =>
        {
            if (_screenshotEnabled)
            {
                // Enabled - pink/active color
                ScreenshotIndicator.Stroke = new SolidColorBrush(Color.FromRgb(255, 107, 157)); // #ff6b9d
                ScreenshotIndicator.Fill = new SolidColorBrush(Color.FromArgb(100, 255, 107, 157));
                ScreenshotButton.ToolTip = "Screenshot Context (enabled)";
            }
            else
            {
                // Disabled - gray
                ScreenshotIndicator.Stroke = new SolidColorBrush(Color.FromRgb(107, 114, 128)); // #6b7280
                ScreenshotIndicator.Fill = new SolidColorBrush(Color.FromArgb(51, 107, 114, 128));
                ScreenshotButton.ToolTip = "Screenshot Context (disabled)";
            }
        });
    }
    
    /// <summary>
    /// Sets the screenshot enabled state without triggering the event.
    /// Used when loading from settings.
    /// </summary>
    public void SetScreenshotEnabled(bool enabled)
    {
        _screenshotEnabled = enabled;
        UpdateScreenshotButtonState();
    }
    
    #endregion
    
    #region State Transitions
    
    private void TransitionToState(BarState newState)
    {
        if (_currentState == newState) return;
        
        var oldState = _currentState;
        _currentState = newState;
        
        Dispatcher.Invoke(() =>
        {
            switch (newState)
            {
                case BarState.Idle:
                    TransitionToIdle();
                    break;
                case BarState.Hovered:
                    TransitionToHovered();
                    break;
                case BarState.Recording:
                    TransitionToRecording();
                    break;
                case BarState.Speaking:
                    TransitionToSpeaking();
                    break;
                case BarState.Processing:
                    TransitionToProcessing();
                    break;
                case BarState.Error:
                    TransitionToError();
                    break;
            }
        });
    }
    
    private void TransitionToIdle()
    {
        // Set bars to gray
        SetBarsColor("#6b7280");
        
        // Contract main bar
        _mainBarContractAnimation?.Begin();
        
        // Hide hint and transcript
        _hintCollapseAnimation?.Begin();
        HideTranscriptBar();
        
        // Stop cursor blink
        _cursorBlinkTimer?.Stop();
        TypingCursor.Visibility = Visibility.Collapsed;
    }
    
    private void TransitionToHovered()
    {
        // Expand main bar slightly
        _mainBarExpandAnimation?.Begin();
        
        // Hide transcript bar first (they share the same position)
        if (_transcriptVisible)
        {
            HideTranscriptBar();
        }
        
        // Show hint bar
        _hintExpandAnimation?.Begin();
        
        // Keep gray color
        SetBarsColor("#8892b0");
    }
    
    private void TransitionToRecording()
    {
        // Expand main bar
        _mainBarExpandAnimation?.Begin();
        
        // Hide hint, we're now recording
        _hintCollapseAnimation?.Begin();
        
        // Set bars to pink/recording color
        SetBarsColor("#ff6b9d");
    }
    
    private void TransitionToSpeaking()
    {
        // Brighter pink color
        SetBarsColor("#ff8eb3");
        
        // Start cursor blink for transcript
        _cursorBlinkTimer?.Start();
        TypingCursor.Visibility = Visibility.Visible;
    }
    
    private void TransitionToProcessing()
    {
        // Change color to processing (cyan)
        SetBarsColor("#00d4aa");
        
        // Stop cursor (processing)
        _cursorBlinkTimer?.Stop();
        TypingCursor.Visibility = Visibility.Collapsed;
    }
    
    private void TransitionToError()
    {
        // Orange/warning color
        SetBarsColor("#ffa502");
    }
    
    private void SetBarsColor(string hexColor)
    {
        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        var brush = new SolidColorBrush(color);
        
        foreach (var bar in _bars)
        {
            bar.Fill = brush;
        }
    }
    
    #endregion
    
    #region Cursor Blink
    
    private void CursorBlinkTimer_Tick(object? sender, EventArgs e)
    {
        _cursorVisible = !_cursorVisible;
        TypingCursor.Opacity = _cursorVisible ? 1.0 : 0.0;
    }
    
    #endregion
    
    #region Public API
    
    /// <summary>
    /// Called when recording starts.
    /// </summary>
    public void ShowRecording(string? mode = null)
    {
        TransitionToState(BarState.Recording);
    }
    
    /// <summary>
    /// Updates the recording time display (currently not shown, but could be added).
    /// </summary>
    public void UpdateRecordingTime(TimeSpan duration)
    {
        // The new design doesn't show recording time, but we keep the API for compatibility
    }
    
    /// <summary>
    /// Called when speech is detected (interim results coming in).
    /// </summary>
    public void ShowSpeaking()
    {
        if (_currentState == BarState.Recording)
        {
            TransitionToState(BarState.Speaking);
        }
    }
    
    /// <summary>
    /// Called when transcribing/polishing starts.
    /// </summary>
    public void ShowProcessing(string? message = null)
    {
        TransitionToState(BarState.Processing);
    }
    
    /// <summary>
    /// Alias for ShowProcessing for compatibility.
    /// </summary>
    public void ShowTranscribing(string? message = null)
    {
        ShowProcessing(message);
    }
    
    /// <summary>
    /// Alias for ShowProcessing for compatibility.
    /// </summary>
    public void ShowPolishing(string? message = null)
    {
        ShowProcessing(message);
    }
    
    /// <summary>
    /// Called when an error occurs. Shows warning bar above transcript bar.
    /// Both bars stay visible for the duration, then fade out together.
    /// </summary>
    public void ShowError(string message, int displaySeconds = 5)
    {
        Dispatcher.Invoke(() =>
        {
            TransitionToState(BarState.Error);
            
            // Hide hint bar (they share the same position)
            HintBar.Opacity = 0;
            
            // Stop any existing warning timers (including fade-complete timer from previous HideWarning)
            _warningHideTimer?.Stop();
            _warningFadeCompleteTimer?.Stop();
            
            // Set error text (no icon, just the message)
            WarningText.Text = message;
            
            // Show warning bar with fade in (above transcript if visible)
            WarningBar.Visibility = Visibility.Visible;
            _warningFadeInAnimation?.Begin();
            
            // Auto-hide both warning and transcript together after displaySeconds
            _warningHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(displaySeconds) };
            _warningHideTimer.Tick += (s, e) =>
            {
                _warningHideTimer.Stop();
                
                // Hide warning bar with fade
                HideWarning();
                
                // Also hide transcript bar with fade
                HideTranscriptBar();
                
                // After animations complete, reset to idle
                var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                resetTimer.Tick += (s2, e2) =>
                {
                    resetTimer.Stop();
                    ResetTranscript();
                    TransitionToState(BarState.Idle);
                };
                resetTimer.Start();
            };
            _warningHideTimer.Start();
        });
    }
    
    /// <summary>
    /// Shows a warning message above the transcript bar (doesn't hide transcript).
    /// Auto-fades out after specified duration.
    /// </summary>
    public void ShowWarning(string message, int autoHideSeconds = 10)
    {
        Dispatcher.Invoke(() =>
        {
            // Stop any existing warning timers (including fade-complete timer from previous HideWarning)
            _warningHideTimer?.Stop();
            _warningFadeCompleteTimer?.Stop();
            
            // Set warning text (no icon, just the message)
            WarningText.Text = message;
            
            // Show warning bar with fade in
            WarningBar.Visibility = Visibility.Visible;
            _warningFadeInAnimation?.Begin();
            
            // Auto-hide after specified seconds
            _warningHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoHideSeconds) };
            _warningHideTimer.Tick += (s, e) =>
            {
                _warningHideTimer.Stop();
                HideWarning();
            };
            _warningHideTimer.Start();
        });
    }
    
    /// <summary>
    /// Hides the warning bar with fade out animation.
    /// </summary>
    private void HideWarning()
    {
        _warningHideTimer?.Stop();
        _warningFadeOutAnimation?.Begin();
        
        // Stop any existing fade complete timer
        _warningFadeCompleteTimer?.Stop();
        
        // Hide after animation completes (tracked timer so it can be cancelled)
        _warningFadeCompleteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _warningFadeCompleteTimer.Tick += (s, e) =>
        {
            _warningFadeCompleteTimer?.Stop();
            WarningBar.Visibility = Visibility.Collapsed;
            WarningBar.Opacity = 0;
        };
        _warningFadeCompleteTimer.Start();
    }
    
    /// <summary>
    /// Updates transcript with interim or final results.
    /// </summary>
    public void UpdateTranscript(string text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        
        Dispatcher.Invoke(() =>
        {
            // Transition to speaking if we receive text
            if (_currentState == BarState.Recording)
            {
                TransitionToState(BarState.Speaking);
            }
            
            if (isFinal)
            {
                // Commit to transcript
                if (!string.IsNullOrEmpty(_committedText) && !_committedText.EndsWith(" "))
                {
                    _committedText += " ";
                }
                _committedText += text.Trim();
                _currentInterim = "";
            }
            else
            {
                // Update interim
                _currentInterim = text.Trim();
            }
            
            // Update display
            var displayText = _committedText;
            if (!string.IsNullOrEmpty(_currentInterim))
            {
                if (!string.IsNullOrEmpty(displayText) && !displayText.EndsWith(" "))
                {
                    displayText += " ";
                }
                displayText += _currentInterim;
            }
            
            TranscriptRun.Text = displayText;
            TranscriptRun.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8892b0"));
            
            // Show transcript bar if not visible
            if (!_transcriptVisible)
            {
                ShowTranscriptBar();
            }
            
            // Show cursor
            TypingCursor.Visibility = Visibility.Visible;
            
            // Scroll to end after layout is complete
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                TranscriptScrollViewer.ScrollToEnd();
            }));
        });
    }
    
    /// <summary>
    /// Hides the bar and resets to idle state.
    /// </summary>
    public void HideAndReset()
    {
        Dispatcher.Invoke(() =>
        {
            ResetTranscript();
            HideTranscriptBar();
            
            // Also hide warning bar if visible and stop all warning timers
            _warningHideTimer?.Stop();
            _warningFadeCompleteTimer?.Stop();
            WarningBar.Visibility = Visibility.Collapsed;
            WarningBar.Opacity = 0;
            
            TransitionToState(BarState.Idle);
        });
    }
    
    /// <summary>
    /// Resets transcript state for new session.
    /// </summary>
    public void ResetTranscript()
    {
        _committedText = "";
        _currentInterim = "";
        _currentAudioLevel = 0;
        _peakAudioLevel = 0;
        TranscriptRun.Text = "";
    }
    
    /// <summary>
    /// Compatibility: same as HideAndReset.
    /// </summary>
    public new void Hide()
    {
        HideAndReset();
    }
    
    #endregion
    
    #region Transcript Bar Visibility and Animation
    
    private void ShowTranscriptBar()
    {
        if (_transcriptVisible) return;
        _transcriptVisible = true;
        
        // Hide hint bar first (they share the same position)
        HintBar.Opacity = 0;
        
        TranscriptBar.Visibility = Visibility.Visible;
        _transcriptExpandAnimation?.Begin();
    }
    
    private void HideTranscriptBar()
    {
        if (!_transcriptVisible) return;
        _transcriptVisible = false;
        
        _transcriptCollapseAnimation?.Begin();
        
        // Hide after animation completes
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            TranscriptBar.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }
    
    #endregion
    
    /// <summary>
    /// Shows without stealing focus.
    /// </summary>
    public void ShowNoActivate()
    {
        base.Show();
        
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
    
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Prevent closing, just reset
        e.Cancel = true;
        HideAndReset();
    }
}
