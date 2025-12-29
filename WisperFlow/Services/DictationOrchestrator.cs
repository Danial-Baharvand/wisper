using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services;

/// <summary>
/// Orchestrates the entire dictation flow: hotkey -> record -> transcribe -> polish -> inject.
/// Acts as the central coordinator between all services.
/// </summary>
public class DictationOrchestrator
{
    private readonly HotkeyManager _hotkeyManager;
    private readonly AudioRecorder _audioRecorder;
    private readonly OpenAITranscriptionClient _transcriptionClient;
    private readonly TextPolisher _textPolisher;
    private readonly TextInjector _textInjector;
    private readonly OverlayWindow _overlayWindow;
    private readonly SettingsManager _settingsManager;
    private readonly ILogger<DictationOrchestrator> _logger;

    private CancellationTokenSource? _currentOperationCts;
    private bool _isEnabled = true;
    private bool _isProcessing;

    public DictationOrchestrator(
        HotkeyManager hotkeyManager,
        AudioRecorder audioRecorder,
        OpenAITranscriptionClient transcriptionClient,
        TextPolisher textPolisher,
        TextInjector textInjector,
        OverlayWindow overlayWindow,
        SettingsManager settingsManager,
        ILogger<DictationOrchestrator> logger)
    {
        _hotkeyManager = hotkeyManager;
        _audioRecorder = audioRecorder;
        _transcriptionClient = transcriptionClient;
        _textPolisher = textPolisher;
        _textInjector = textInjector;
        _overlayWindow = overlayWindow;
        _settingsManager = settingsManager;
        _logger = logger;

        // Wire up event handlers
        _hotkeyManager.RecordStart += OnRecordStart;
        _hotkeyManager.RecordStop += OnRecordStop;
        _audioRecorder.MaxDurationReached += OnMaxDurationReached;
        _audioRecorder.RecordingProgress += OnRecordingProgress;
    }

    /// <summary>
    /// Applies settings to all services.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _isEnabled = settings.HotkeyEnabled;
        _audioRecorder.SetMaxDuration(settings.MaxRecordingDurationSeconds);

        // Set microphone device if specified
        if (!string.IsNullOrEmpty(settings.MicrophoneDeviceId))
        {
            if (int.TryParse(settings.MicrophoneDeviceId, out int deviceNumber))
            {
                _audioRecorder.SetDevice(deviceNumber);
            }
        }

        _logger.LogInformation("Settings applied: Enabled={Enabled}, Polish={Polish}, NotesMode={Notes}",
            settings.HotkeyEnabled, settings.PolishOutput, settings.NotesMode);
    }

    /// <summary>
    /// Enables or disables dictation.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
        if (!enabled && _audioRecorder.IsRecording)
        {
            // Cancel current operation
            _currentOperationCts?.Cancel();
            _audioRecorder.StopRecording();
            _overlayWindow.Hide();
        }
        _logger.LogInformation("Dictation {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Updates hotkey configuration.
    /// </summary>
    public void UpdateHotkey(HotkeyModifiers modifiers, int key)
    {
        _hotkeyManager.UnregisterHotkey();
        _hotkeyManager.RegisterHotkey(modifiers, key);
    }

    private void OnRecordStart(object? sender, EventArgs e)
    {
        if (!_isEnabled || _isProcessing)
        {
            _logger.LogDebug("Recording start ignored: Enabled={Enabled}, Processing={Processing}",
                _isEnabled, _isProcessing);
            return;
        }

        try
        {
            _currentOperationCts = new CancellationTokenSource();
            _audioRecorder.StartRecording();
            _overlayWindow.ShowRecording();
            _logger.LogInformation("Recording started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            _overlayWindow.ShowError("Failed to start recording");
        }
    }

    private async void OnRecordStop(object? sender, EventArgs e)
    {
        if (!_audioRecorder.IsRecording)
        {
            return;
        }

        var audioFilePath = _audioRecorder.StopRecording();
        if (string.IsNullOrEmpty(audioFilePath))
        {
            _overlayWindow.Hide();
            return;
        }

        await ProcessRecordingAsync(audioFilePath);
    }

    private void OnMaxDurationReached(object? sender, EventArgs e)
    {
        _overlayWindow.ShowError("Max duration reached");
    }

    private void OnRecordingProgress(object? sender, TimeSpan duration)
    {
        _overlayWindow.UpdateRecordingTime(duration);
    }

    private async Task ProcessRecordingAsync(string audioFilePath)
    {
        _isProcessing = true;
        var settings = _settingsManager.CurrentSettings;
        var cts = _currentOperationCts;

        try
        {
            _overlayWindow.ShowTranscribing();
            _logger.LogInformation("Processing recording...");

            // Transcribe
            var transcript = await _transcriptionClient.TranscribeAsync(
                audioFilePath,
                settings.Language,
                settings.CustomPrompt,
                cts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                _logger.LogWarning("Transcription returned empty");
                _overlayWindow.ShowError("No speech detected");
                return;
            }

            _logger.LogDebug("Raw transcript: {Length} chars", transcript.Length);

            // Polish if enabled
            var finalText = transcript;
            if (settings.PolishOutput)
            {
                _overlayWindow.ShowPolishing();
                finalText = await _textPolisher.PolishAsync(
                    transcript,
                    settings.NotesMode,
                    cts?.Token ?? CancellationToken.None);
                _logger.LogDebug("Polished text: {Length} chars", finalText.Length);
            }

            // Inject text
            _overlayWindow.Hide();
            await _textInjector.InjectTextAsync(finalText, cts?.Token ?? CancellationToken.None);

            _logger.LogInformation("Dictation complete, injected {Length} chars", finalText.Length);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Dictation cancelled");
            _overlayWindow.Hide();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Dictation failed: {Message}", ex.Message);
            _overlayWindow.ShowError(TruncateError(ex.Message));
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error during dictation: {Message}", ex.Message);
            var message = ex.StatusCode.HasValue 
                ? $"Network error ({(int)ex.StatusCode}): {ex.Message}"
                : $"Network error: {ex.Message}";
            _overlayWindow.ShowError(TruncateError(message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dictation failed unexpectedly: {Type} - {Message}", ex.GetType().Name, ex.Message);
            _overlayWindow.ShowError($"Error: {TruncateError(ex.Message)}");
        }
        finally
        {
            _isProcessing = false;
            
            // Clean up temp file
            _audioRecorder.DeleteTempFile(audioFilePath);
            
            // Dispose CTS
            if (cts == _currentOperationCts)
            {
                _currentOperationCts = null;
            }
            cts?.Dispose();
        }
    }

    private static string TruncateError(string message)
    {
        // Truncate for overlay display (max ~60 chars)
        if (message.Length <= 60) return message;
        return message[..57] + "...";
    }
}

