using System.Windows;
using System.Windows.Media.Animation;

namespace WisperFlow;

/// <summary>
/// Overlay window showing recording/transcribing status.
/// Positioned at the top-center of the primary screen.
/// </summary>
public partial class OverlayWindow : Window
{
    private Storyboard? _pulseAnimation;
    private Storyboard? _spinAnimation;
    private System.Timers.Timer? _errorHideTimer;

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

        // Start hidden
        Hide();
    }

    /// <summary>
    /// Shows the recording state with pulsing red dot.
    /// </summary>
    public void ShowRecording()
    {
        Dispatcher.Invoke(() =>
        {
            HideAllPanels();
            RecordingPanel.Visibility = Visibility.Visible;
            RecordingTime.Text = "";
            
            _pulseAnimation?.Begin();
            Show();
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
    public void ShowTranscribing()
    {
        Dispatcher.Invoke(() =>
        {
            HideAllPanels();
            TranscribingPanel.Visibility = Visibility.Visible;
            TranscribingText.Text = "Transcribing...";
            
            _pulseAnimation?.Stop();
            _spinAnimation?.Begin();
            Show();
        });
    }

    /// <summary>
    /// Shows the polishing state.
    /// </summary>
    public void ShowPolishing()
    {
        Dispatcher.Invoke(() =>
        {
            TranscribingText.Text = "Polishing...";
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
            Show();

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

