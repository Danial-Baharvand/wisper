using System.IO;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using WisperFlow.Services.CodeContext;
using WisperFlow.Services.CodeDictation;
using WisperFlow.Services.Polish;
using WisperFlow.Services.Transcription;

namespace WisperFlow.Services;

/// <summary>
/// Orchestrates the entire dictation flow: hotkey -> record -> transcribe -> polish/code -> inject.
/// Supports three modes: Regular Dictation, Command Mode, and Code Dictation.
/// </summary>
public class DictationOrchestrator
{
    private readonly HotkeyManager _hotkeyManager;
    private readonly AudioRecorder _audioRecorder;
    private readonly TextInjector _textInjector;
    private readonly DictationBar _dictationBar;  // Unified bottom bar UI
    private readonly SettingsManager _settingsManager;
    private readonly ServiceFactory _serviceFactory;
    private readonly CodeContextService _codeContextService;
    private readonly ScreenshotService _screenshotService;
    private readonly ILogger<DictationOrchestrator> _logger;

    private ITranscriptionService? _transcriptionService;
    private IPolishService? _polishService;
    private IPolishService? _commandModeService;  // Separate LLM service for command mode
    private ICodeDictationService? _codeDictationService;
    private DeepgramStreamingService? _streamingService;  // For real-time streaming
    private CancellationTokenSource? _currentOperationCts;
    private bool _isEnabled = true;
    private bool _isProcessing;
    private bool _servicesInitializing;
    private bool _isMaxDurationScenario;  // Track if recording stopped due to max duration
    private string? _commandModeSelectedText;  // Selected text captured at command stop
    private string? _commandModeSearchContext;  // Highlighted text for search context (when not in textbox)
    private bool _commandModeTextInputFocused;  // Whether a text input was focused
    private bool _isStreamingActive;  // Track if Deepgram streaming is active
    private byte[]? _currentScreenshot;  // Screenshot captured at hotkey press (for command mode)

    public DictationOrchestrator(
        HotkeyManager hotkeyManager,
        AudioRecorder audioRecorder,
        TextInjector textInjector,
        DictationBar dictationBar,
        SettingsManager settingsManager,
        ServiceFactory serviceFactory,
        CodeContextService codeContextService,
        ScreenshotService screenshotService,
        ILogger<DictationOrchestrator> logger)
    {
        _hotkeyManager = hotkeyManager;
        _audioRecorder = audioRecorder;
        _textInjector = textInjector;
        _dictationBar = dictationBar;
        _settingsManager = settingsManager;
        _serviceFactory = serviceFactory;
        _codeContextService = codeContextService;
        _screenshotService = screenshotService;
        _logger = logger;

        _hotkeyManager.RecordStart += OnRecordStart;
        _hotkeyManager.RecordStop += OnRecordStop;
        _hotkeyManager.CommandRecordStart += OnCommandRecordStart;
        _hotkeyManager.CommandRecordStop += OnCommandRecordStop;
        _hotkeyManager.CodeDictationRecordStart += OnCodeDictationRecordStart;
        _hotkeyManager.CodeDictationRecordStop += OnCodeDictationRecordStop;
        _audioRecorder.MaxDurationReached += OnMaxDurationReached;
        _audioRecorder.WarningDurationReached += OnWarningDurationReached;
        _audioRecorder.RecordingProgress += OnRecordingProgress;
        _audioRecorder.AudioLevelChanged += OnAudioLevelChanged;
        
        // Subscribe to screenshot capture from user selection
        _dictationBar.ContextScreenshotCaptured += OnContextScreenshotCaptured;
    }

    public void ApplySettings(AppSettings settings)
    {
        _isEnabled = settings.HotkeyEnabled;

        if (!string.IsNullOrEmpty(settings.MicrophoneDeviceId) && 
            int.TryParse(settings.MicrophoneDeviceId, out int deviceNumber))
        {
            _audioRecorder.SetDevice(deviceNumber);
        }

        // Update services if model changed
        if (_transcriptionService?.ModelId != settings.TranscriptionModelId)
        {
            _transcriptionService?.Dispose();
            _transcriptionService = _serviceFactory.CreateTranscriptionService(settings.TranscriptionModelId);
            _logger.LogInformation("Transcription model: {Model}", settings.TranscriptionModelId);
        }

        if (_polishService?.ModelId != settings.PolishModelId)
        {
            _polishService?.Dispose();
            _polishService = _serviceFactory.CreatePolishService(settings.PolishModelId);
            _logger.LogInformation("Polish model: {Model}", settings.PolishModelId);
        }

        // Update command mode service if model changed (separate from polish)
        if (_commandModeService?.ModelId != settings.CommandModeModelId)
        {
            _commandModeService?.Dispose();
            _commandModeService = _serviceFactory.CreatePolishService(settings.CommandModeModelId);
            _logger.LogInformation("Command mode model: {Model}", settings.CommandModeModelId);
        }

        // Update code dictation service if model changed
        if (_codeDictationService?.ModelId != settings.CodeDictationModelId)
        {
            _codeDictationService?.Dispose();
            _codeDictationService = _serviceFactory.CreateCodeDictationService(settings.CodeDictationModelId);
            _logger.LogInformation("Code dictation model: {Model}", settings.CodeDictationModelId);
        }

        // Register command mode hotkey
        _hotkeyManager.RegisterCommandHotkey(settings.CommandHotkeyModifiers, settings.CommandModeEnabled);
        
        // Register code dictation hotkey
        _hotkeyManager.RegisterCodeDictationHotkey(settings.CodeDictationHotkeyModifiers, settings.CodeDictationEnabled);

        // Update hotkey display in the dictation bar
        var hotkeyDisplay = FormatHotkeyDisplay(settings.HotkeyModifiers, settings.HotkeyKey);
        _dictationBar.SetHotkey(hotkeyDisplay);
        
        _logger.LogInformation("Settings applied: Enabled={Enabled}, Polish={Polish}, CommandMode={CommandMode}, CodeDictation={CodeDictation}", 
            settings.HotkeyEnabled, settings.PolishOutput, settings.CommandModeEnabled, settings.CodeDictationEnabled);
    }
    
    private static string FormatHotkeyDisplay(HotkeyModifiers modifiers, int key)
    {
        var parts = new List<string>();
        
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("win");
        
        // Add the key if specified
        if (key > 0)
        {
            // Convert virtual key code to string
            var keyName = ((System.Windows.Forms.Keys)key).ToString().ToLower();
            if (keyName.StartsWith("capital"))
                keyName = "caps";
            parts.Add(keyName);
        }
        
        return string.Join("+", parts);
    }

    public async Task InitializeServicesAsync()
    {
        var settings = _settingsManager.CurrentSettings;
        _transcriptionService = _serviceFactory.CreateTranscriptionService(settings.TranscriptionModelId);
        _polishService = _serviceFactory.CreatePolishService(settings.PolishModelId);

        // Pre-initialize Whisper/faster-whisper (LLM is loaded on demand due to size)
        _ = Task.Run(async () =>
        {
            try
            {
                _servicesInitializing = true;
                // Pre-load any local transcription service (LocalWhisperService or FasterWhisperService)
                if (_transcriptionService is LocalWhisperService or FasterWhisperService)
                {
                    _logger.LogInformation("Pre-loading transcription model...");
                    await _transcriptionService.InitializeAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pre-initialize services");
            }
            finally
            {
                _servicesInitializing = false;
            }
        });
    }

    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
        if (!enabled && _audioRecorder.IsRecording)
        {
            _currentOperationCts?.Cancel();
            _audioRecorder.StopRecording();
            _dictationBar.Hide();
        }
        _logger.LogInformation("Dictation {State}", enabled ? "enabled" : "disabled");
    }

    public void UpdateHotkey(HotkeyModifiers modifiers, int key)
    {
        _hotkeyManager.UnregisterHotkey();
        _hotkeyManager.RegisterHotkey(modifiers, key);
    }

    private void OnRecordStart(object? sender, EventArgs e)
    {
        if (!_isEnabled || _isProcessing) return;
        if (_servicesInitializing)
        {
            _dictationBar.ShowError("Models loading...");
            return;
        }

        try
        {
            _currentOperationCts = new CancellationTokenSource();
            var settings = _settingsManager.CurrentSettings;
            
            // Check if we should use Deepgram streaming
            var useStreaming = settings.DeepgramStreaming && 
                               settings.TranscriptionModelId.StartsWith("deepgram-") &&
                               !string.IsNullOrEmpty(CredentialManager.GetDeepgramApiKey());
            
            if (useStreaming)
            {
                // Start streaming transcription - connection happens in background!
                var model = ModelCatalog.GetById(settings.TranscriptionModelId);
                if (model != null)
                {
                    _streamingService = new DeepgramStreamingService(
                        _logger, model, settings);
                    
                    try
                    {
                        // Get code context keywords if a supported editor is active
                        // Use Task.Run to avoid potential deadlocks with COM interop on UI thread
                        List<string>? codeKeywords = null;
                        if (_codeContextService.IsSupportedEditorActive())
                        {
                            try
                            {
                                // Run on thread pool to avoid COM/UI thread issues
                                // Use a short timeout to not delay recording start too much
                                var keywordTask = Task.Run(() => _codeContextService.GetKeywordsForDeepgramAsync());
                                if (keywordTask.Wait(500))  // 500ms timeout
                                {
                                    codeKeywords = keywordTask.Result;
                                    _logger.LogInformation("Got {Count} code context keywords for Deepgram streaming", 
                                        codeKeywords.Count);
                                }
                                else
                                {
                                    _logger.LogDebug("Keyword extraction timed out, proceeding without keywords");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to get code context keywords");
                            }
                        }
                        
                        // Subscribe to streaming events for real-time UI
                        _streamingService.OnTranscriptUpdate += OnTranscriptUpdateReceived;
                        _streamingService.OnUtteranceEnd += OnUtteranceEndReceived;
                        
                        // Reset transcript for new session
                        _dictationBar.ResetTranscript();
                        
                        // This returns immediately - connection happens in background
                        // Audio is buffered until connection completes
                        _streamingService.StartStreamingAsync(
                            settings.Language == "auto" ? "en" : settings.Language,
                            codeKeywords,
                            _currentOperationCts.Token);
                        
                        // Subscribe to audio chunks immediately
                        _audioRecorder.AudioDataAvailable += OnAudioDataForStreaming;
                        _isStreamingActive = true;
                        _logger.LogInformation("Deepgram streaming initiated with {Keywords} code keywords", 
                            codeKeywords?.Count ?? 0);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to initiate streaming, falling back to batch");
                        _streamingService?.Dispose();
                        _streamingService = null;
                        _isStreamingActive = false;
                    }
                }
            }
            
            // Start recording IMMEDIATELY - no delay!
            _audioRecorder.StartRecording();
            _dictationBar.ShowRecording();
            _logger.LogInformation("Recording started (streaming: {Streaming})", _isStreamingActive);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            _dictationBar.ShowError("Failed to start recording");
            _isStreamingActive = false;
        }
    }
    
    private async void OnAudioDataForStreaming(object? sender, byte[] audioData)
    {
        if (_streamingService != null && _isStreamingActive)
        {
            try
            {
                await _streamingService.SendAudioChunkAsync(audioData, _currentOperationCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send audio chunk to streaming service");
            }
        }
    }
    
    /// <summary>
    /// Called when a transcript update is received from Deepgram (interim or final).
    /// Shows it in the dictation bar in real-time.
    /// </summary>
    private void OnTranscriptUpdateReceived(string text, bool isFinal)
    {
        try
        {
            _dictationBar.UpdateTranscript(text, isFinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update dictation bar transcript");
        }
    }
    
    /// <summary>
    /// Called when Deepgram signals end of utterance (speech stopped).
    /// </summary>
    private void OnUtteranceEndReceived()
    {
        // Currently just logged, UI state is managed by the transcript updates
        _logger.LogDebug("Utterance end received");
    }

    private async void OnRecordStop(object? sender, EventArgs e)
    {
        // Multiple guards to prevent double-processing
        if (!_audioRecorder.IsRecording) return;
        if (_isProcessing) return;  // Already processing a previous recording
        
        // Set flag immediately to prevent any race conditions
        _isProcessing = true;

        // Transition to processing state (if streaming was active)
        if (_isStreamingActive)
        {
            _dictationBar.ShowProcessing();
        }

        // Unsubscribe from audio events
        _audioRecorder.AudioDataAvailable -= OnAudioDataForStreaming;
        
        var audioFilePath = _audioRecorder.StopRecording();
        if (string.IsNullOrEmpty(audioFilePath))
        {
            _dictationBar.Hide();
            CleanupStreaming();
            _isProcessing = false;  // Reset flag
            return;
        }

        // If streaming was active, get transcript from streaming service
        if (_isStreamingActive && _streamingService != null)
        {
            await ProcessStreamingRecordingAsync(audioFilePath);
        }
        else
        {
            await ProcessRecordingAsync(audioFilePath);
        }
    }
    
    private void CleanupStreaming()
    {
        _audioRecorder.AudioDataAvailable -= OnAudioDataForStreaming;
        
        // Unsubscribe from streaming events
        if (_streamingService != null)
        {
            _streamingService.OnTranscriptUpdate -= OnTranscriptUpdateReceived;
            _streamingService.OnUtteranceEnd -= OnUtteranceEndReceived;
        }
        
        _streamingService?.Dispose();
        _streamingService = null;
        _isStreamingActive = false;
    }
    
    private async Task ProcessStreamingRecordingAsync(string audioFilePath)
    {
        // _isProcessing already set in OnRecordStop
        var settings = _settingsManager.CurrentSettings;
        var cts = _currentOperationCts;

        try
        {
            _dictationBar.ShowTranscribing("Finalizing...");
            
            // Get transcript from streaming service
            var transcript = await _streamingService!.StopStreamingAsync(cts?.Token ?? CancellationToken.None);
            
            if (string.IsNullOrWhiteSpace(transcript))
            {
                _logger.LogWarning("Streaming returned empty transcript, falling back to batch");
                // Fall back to batch transcription
                CleanupStreaming();
                await ProcessRecordingAsync(audioFilePath);
                return;
            }

            _logger.LogInformation("Streaming transcript: {Len} chars", transcript.Length);

            var finalText = transcript;
            if (settings.PolishOutput)
            {
                _polishService ??= _serviceFactory.CreatePolishService(settings.PolishModelId);
                
                if (!_polishService.IsReady)
                {
                    _dictationBar.ShowPolishing("Loading model...");
                    await _polishService.InitializeAsync(cts?.Token ?? CancellationToken.None);
                }

                _dictationBar.ShowPolishing();
                finalText = await _polishService.PolishAsync(transcript, settings.NotesMode, cts?.Token ?? CancellationToken.None);
                _logger.LogDebug("Polished: {Len} chars", finalText.Length);
            }

            // If max duration scenario, wait for user to release hotkeys before pasting
            if (_isMaxDurationScenario)
            {
                var hotkeysReleased = await _textInjector.WaitForHotkeysReleasedAsync(
                    timeoutSeconds: 10,
                    onWaitingStarted: () => _dictationBar.ShowWarning("Release Ctrl+Win to paste...", autoHideSeconds: 10)
                );
                
                if (!hotkeysReleased)
                {
                    // Timeout - set clipboard and show message but don't paste
                    _logger.LogWarning("Hotkey release timeout - text left in clipboard");
                    
                    // Set clipboard so user can paste manually
                    System.Windows.Clipboard.SetText(finalText);
                    
                    _dictationBar.ShowError("Insertion cancelled. Paste with Ctrl+V.");
                    return;
                }
            }
            
            _dictationBar.Hide();
            
            // Get file names for @ mention detection and Tab tagging (only file names, not symbols)
            var fileNames = _codeContextService.IsSupportedEditorActive() 
                ? _codeContextService.GetFileNamesForMentions() 
                : null;
            await _textInjector.InjectTextWithMentionsAsync(finalText, fileNames, cts?.Token ?? CancellationToken.None);
            _logger.LogInformation("Injected {Len} chars (via streaming)", finalText.Length);
        }
        catch (OperationCanceledException)
        {
            _dictationBar.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streaming transcription error: {Msg}", ex.Message);
            _dictationBar.ShowError("Error: " + (ex.Message.Length > 50 ? ex.Message[..47] + "..." : ex.Message));
        }
        finally
        {
            _isProcessing = false;
            _isMaxDurationScenario = false;  // Reset max duration flag
            CleanupStreaming();
            _audioRecorder.DeleteTempFile(audioFilePath);
            if (cts == _currentOperationCts) _currentOperationCts = null;
            cts?.Dispose();
        }
    }

    private void OnWarningDurationReached(object? sender, EventArgs e)
    {
        int remainingSeconds = _audioRecorder.GetRemainingSeconds();
        string message = remainingSeconds >= 60 
            ? $"{remainingSeconds / 60} minute{(remainingSeconds >= 120 ? "s" : "")} remaining"
            : $"{remainingSeconds}s remaining";
        
        _logger.LogWarning("Recording duration warning - {Message}", message);
        // Use ShowWarning to display above transcript without replacing it
        _dictationBar.ShowWarning(message, autoHideSeconds: 10);
    }
    
    private async void OnMaxDurationReached(object? sender, EventArgs e)
    {
        _logger.LogWarning("Max duration reached - processing recording");
        
        // Don't process if already processing
        if (_isProcessing) return;
        _isProcessing = true;
        _isMaxDurationScenario = true;  // Mark this as a max duration stop
        
        // Show processing state
        _dictationBar.ShowProcessing("Processing...");
        
        // Unsubscribe from audio events
        _audioRecorder.AudioDataAvailable -= OnAudioDataForStreaming;
        
        // StopRecording was already called by AudioRecorder before firing this event
        // Get the file path from the recorder
        var audioFilePath = _audioRecorder.CurrentFilePath;
        
        if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
        {
            _logger.LogError("Could not find audio file after max duration");
            _dictationBar.ShowError("Recording failed");
            CleanupStreaming();
            _isProcessing = false;
            return;
        }
        
        _logger.LogInformation("Processing max duration recording: {Path}", audioFilePath);
        
        // Process the recording
        if (_isStreamingActive && _streamingService != null)
        {
            await ProcessStreamingRecordingAsync(audioFilePath);
        }
        else
        {
            await ProcessRecordingAsync(audioFilePath);
        }
    }
    
    private void OnRecordingProgress(object? sender, TimeSpan duration) => _dictationBar.UpdateRecordingTime(duration);
    private void OnAudioLevelChanged(object? sender, float level) => _dictationBar.UpdateAudioLevel(level);

    private async Task ProcessRecordingAsync(string audioFilePath)
    {
        // _isProcessing already set in OnRecordStop
        var settings = _settingsManager.CurrentSettings;
        var cts = _currentOperationCts;

        try
        {
            _transcriptionService ??= _serviceFactory.CreateTranscriptionService(settings.TranscriptionModelId);
            _polishService ??= _serviceFactory.CreatePolishService(settings.PolishModelId);

            // Initialize if needed
            if (!_transcriptionService.IsReady)
            {
                _dictationBar.ShowTranscribing("Loading model...");
                await _transcriptionService.InitializeAsync(cts?.Token ?? CancellationToken.None);
            }

            _dictationBar.ShowTranscribing();
            _logger.LogInformation("Processing with {Model}...", _transcriptionService.ModelId);

            // Get code context keywords for Deepgram transcription
            string transcript;
            if (_transcriptionService is DeepgramTranscriptionService deepgramService && 
                _codeContextService.IsSupportedEditorActive())
            {
                var keywords = await _codeContextService.GetKeywordsForDeepgramAsync();
                _logger.LogInformation("Using {Count} code context keywords for Deepgram batch transcription", keywords.Count);
                
                transcript = await deepgramService.TranscribeWithKeywordsAsync(
                    audioFilePath,
                    settings.Language == "auto" ? null : settings.Language,
                    keywords,
                    cts?.Token ?? CancellationToken.None);
            }
            else
            {
                transcript = await _transcriptionService.TranscribeAsync(
                    audioFilePath,
                    settings.Language == "auto" ? null : settings.Language,
                    cts?.Token ?? CancellationToken.None);
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                _dictationBar.ShowError("No speech detected");
                return;
            }

            _logger.LogDebug("Transcript: {Len} chars", transcript.Length);

            var finalText = transcript;
            if (settings.PolishOutput)
            {
                if (!_polishService.IsReady)
                {
                    _dictationBar.ShowPolishing("Loading model...");
                    await _polishService.InitializeAsync(cts?.Token ?? CancellationToken.None);
                }

                _dictationBar.ShowPolishing();
                finalText = await _polishService.PolishAsync(transcript, settings.NotesMode, cts?.Token ?? CancellationToken.None);
                _logger.LogDebug("Polished: {Len} chars", finalText.Length);
            }

            // If max duration scenario, wait for user to release hotkeys before pasting
            if (_isMaxDurationScenario)
            {
                var hotkeysReleased = await _textInjector.WaitForHotkeysReleasedAsync(
                    timeoutSeconds: 10,
                    onWaitingStarted: () => _dictationBar.ShowWarning("Release Ctrl+Win to paste...", autoHideSeconds: 10)
                );
                
                if (!hotkeysReleased)
                {
                    // Timeout - set clipboard and show message but don't paste
                    _logger.LogWarning("Hotkey release timeout - text left in clipboard");
                    
                    // Set clipboard so user can paste manually
                    System.Windows.Clipboard.SetText(finalText);
                    
                    _dictationBar.ShowError("Insertion cancelled. Paste with Ctrl+V.");
                    return;
                }
            }
            
            _dictationBar.Hide();
            
            // Get file names for @ mention detection and Tab tagging (only file names, not symbols)
            var fileNames = _codeContextService.IsSupportedEditorActive() 
                ? _codeContextService.GetFileNamesForMentions() 
                : null;
            await _textInjector.InjectTextWithMentionsAsync(finalText, fileNames, cts?.Token ?? CancellationToken.None);
            _logger.LogInformation("Injected {Len} chars", finalText.Length);
        }
        catch (OperationCanceledException)
        {
            _dictationBar.Hide();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Dictation failed: {Msg}", ex.Message);
            _dictationBar.ShowError(ex.Message.Length > 60 ? ex.Message[..57] + "..." : ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dictation error: {Msg}", ex.Message);
            _dictationBar.ShowError("Error: " + (ex.Message.Length > 50 ? ex.Message[..47] + "..." : ex.Message));
        }
        finally
        {
            _isProcessing = false;
            _isMaxDurationScenario = false;  // Reset max duration flag
            _audioRecorder.DeleteTempFile(audioFilePath);
            if (cts == _currentOperationCts) _currentOperationCts = null;
            cts?.Dispose();
        }
    }

    // ===== Command Mode Handlers =====

    private void OnCommandRecordStart(object? sender, EventArgs e)
    {
        if (!_isEnabled || _isProcessing) return;
        if (_servicesInitializing)
        {
            _dictationBar.ShowError("Models loading...");
            return;
        }

        try
        {
            _currentOperationCts = new CancellationTokenSource();
            _commandModeSelectedText = null;  // Will be captured on stop
            
            // Screenshot is now captured via user selection during recording
            // The ContextScreenshotCaptured event will set _currentScreenshot
            _currentScreenshot = null;
            
            // Start recording and show overlay IMMEDIATELY
            _dictationBar.ShowRecording("Command Mode");
            _audioRecorder.StartRecording();
            _logger.LogInformation("Command recording started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start command recording");
            _dictationBar.ShowError("Failed to start recording");
            _commandModeSelectedText = null;
            _currentScreenshot = null;
        }
    }
    
    /// <summary>
    /// Called when user captures a screenshot during recording.
    /// </summary>
    private void OnContextScreenshotCaptured(object? sender, byte[] screenshotBytes)
    {
        _currentScreenshot = screenshotBytes;
        _logger.LogInformation("Context screenshot captured via selection: {Size} bytes", screenshotBytes.Length);
    }

    private async void OnCommandRecordStop(object? sender, EventArgs e)
    {
        // Multiple guards to prevent double-processing
        if (!_audioRecorder.IsRecording) return;
        if (_isProcessing) return;
        
        _isProcessing = true;

        var audioFilePath = _audioRecorder.StopRecording();
        if (string.IsNullOrEmpty(audioFilePath))
        {
            _dictationBar.Hide();
            _isProcessing = false;
            return;
        }

        // Check if a text input is focused FIRST (before any clipboard operations)
        _commandModeTextInputFocused = ClipboardHelper.IsTextInputFocused();
        _logger.LogInformation("Text input focused: {Focused}", _commandModeTextInputFocused);

        // Reset state
        _commandModeSelectedText = null;
        _commandModeSearchContext = null;

        if (_commandModeTextInputFocused)
        {
            // Text input is focused - try to capture selected text for TRANSFORM
            _logger.LogDebug("Text input focused - attempting to capture selected text...");
            _commandModeSelectedText = await ClipboardHelper.GetSelectedTextAsync();
            
            if (!string.IsNullOrWhiteSpace(_commandModeSelectedText))
            {
                _logger.LogInformation("Command mode: TRANSFORM - captured {Len} chars of selected text", 
                    _commandModeSelectedText.Length);
            }
            else
            {
                _logger.LogInformation("Command mode: GENERATE - text input focused, no selection");
            }
        }
        else
        {
            // No text input focused - this will be SEARCH mode
            // But try to capture any highlighted text as context for the search
            _logger.LogDebug("No text input - attempting to capture highlighted text for search context...");
            _commandModeSearchContext = await ClipboardHelper.GetSelectedTextAsync();
            
            if (!string.IsNullOrWhiteSpace(_commandModeSearchContext))
            {
                _logger.LogInformation("Command mode: SEARCH with context - captured {Len} chars of highlighted text", 
                    _commandModeSearchContext.Length);
            }
            else
            {
                _logger.LogInformation("Command mode: SEARCH - no highlighted text");
            }
        }

        await ProcessCommandRecordingAsync(audioFilePath);
    }

    private async Task ProcessCommandRecordingAsync(string audioFilePath)
    {
        // _isProcessing already set in OnCommandRecordStop
        var settings = _settingsManager.CurrentSettings;
        var cts = _currentOperationCts;

        try
        {
            _transcriptionService ??= _serviceFactory.CreateTranscriptionService(settings.TranscriptionModelId);
            _commandModeService ??= _serviceFactory.CreatePolishService(settings.CommandModeModelId);

            // Initialize transcription if needed
            if (!_transcriptionService.IsReady)
            {
                _dictationBar.ShowTranscribing("Loading model...");
                await _transcriptionService.InitializeAsync(cts?.Token ?? CancellationToken.None);
            }

            _dictationBar.ShowTranscribing("Processing command...");
            _logger.LogInformation("Processing command with {Model}...", _transcriptionService.ModelId);

            // Transcribe the spoken command
            var rawCommand = await _transcriptionService.TranscribeAsync(
                audioFilePath,
                settings.Language == "auto" ? null : settings.Language,
                cts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(rawCommand))
            {
                _dictationBar.ShowError("No command detected");
                return;
            }

            _logger.LogInformation("Raw command: {Command}", rawCommand);

            // Use raw command directly - don't polish user instructions
            var command = rawCommand.Trim();

            // Decide mode based on text input focus and selected text
            if (!string.IsNullOrWhiteSpace(_commandModeSelectedText))
            {
                // Mode 1: Text was selected - transform it according to the command
                _logger.LogInformation("Mode: Transform selected text");
                await ProcessTextTransformAsync(_commandModeSelectedText, command, cts?.Token ?? CancellationToken.None);
            }
            else if (_commandModeTextInputFocused)
            {
                // Mode 2: Text input focused but no text selected - generate new text
                _logger.LogInformation("Mode: Generate new text");
                await ProcessTextGenerateAsync(command, cts?.Token ?? CancellationToken.None);
            }
            else
            {
                // Mode 3: No text input focused - open in browser
                // If there's highlighted text, include it as context
                string searchQuery;
                if (!string.IsNullOrWhiteSpace(_commandModeSearchContext))
                {
                    // Format: "User's question\n\nHighlighted text as context"
                    searchQuery = $"{command}\n\n{_commandModeSearchContext}";
                    _logger.LogInformation("Mode: Search with context ({ContextLen} chars)", _commandModeSearchContext.Length);
                }
                else
                {
                    searchQuery = command;
                    _logger.LogInformation("Mode: Search (no context)");
                }

                _dictationBar.Hide();

                // Use embedded browser for ChatGPT and Gemini
                var selectedProvider = _dictationBar.SelectedProvider;
                if (selectedProvider is "ChatGPT" or "Gemini")
                {
                    // Pass screenshot for context if captured
                    await _dictationBar.OpenAndQueryAsync(selectedProvider, searchQuery, _currentScreenshot);
                    _logger.LogInformation("Opened query in embedded {Provider}: {Query}, Screenshot: {HasScreenshot}", 
                        selectedProvider,
                        searchQuery.Length > 50 ? searchQuery[..50] + "..." : searchQuery,
                        _currentScreenshot != null);
                }
                else
                {
                    // Fallback to external browser for other services
                    await BrowserQueryService.OpenQueryAsync(searchQuery, settings.CommandModeSearchEngine);
                    _logger.LogInformation("Opened query in {Service}: {Query}", settings.CommandModeSearchEngine,
                        searchQuery.Length > 50 ? searchQuery[..50] + "..." : searchQuery);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _dictationBar.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command processing error: {Msg}", ex.Message);
            _dictationBar.ShowError("Error: " + (ex.Message.Length > 50 ? ex.Message[..47] + "..." : ex.Message));
        }
        finally
        {
            _isProcessing = false;
            _commandModeSelectedText = null;  // Clear captured text
            _commandModeSearchContext = null;  // Clear search context
            _currentScreenshot = null;  // Clear screenshot
            _audioRecorder.DeleteTempFile(audioFilePath);
            if (cts == _currentOperationCts) _currentOperationCts = null;
            cts?.Dispose();
        }
    }

    private async Task ProcessTextTransformAsync(string selectedText, string command, CancellationToken cancellationToken)
    {
        _dictationBar.ShowPolishing("Transforming...");
        _logger.LogInformation("Transforming {Len} chars with command: '{Command}'", selectedText.Length, command);
        _logger.LogDebug("Original text: '{Text}'", selectedText.Length > 100 ? selectedText[..100] + "..." : selectedText);

        var settings = _settingsManager.CurrentSettings;
        _commandModeService ??= _serviceFactory.CreatePolishService(settings.CommandModeModelId);

        if (!_commandModeService.IsReady)
        {
            _logger.LogInformation("Initializing command mode service...");
            await _commandModeService.InitializeAsync(cancellationToken);
        }

        _logger.LogDebug("Calling TransformAsync...");
        string transformedText;
        try
        {
            transformedText = await _commandModeService.TransformAsync(selectedText, command, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Transform failed - polish service not ready");
            _dictationBar.ShowError("Polish model failed. Use OpenAI in settings.");
            return;
        }
        
        _logger.LogDebug("TransformAsync returned: '{Text}'", 
            string.IsNullOrEmpty(transformedText) ? "(empty)" : 
            (transformedText.Length > 100 ? transformedText[..100] + "..." : transformedText));

        if (string.IsNullOrWhiteSpace(transformedText))
        {
            _logger.LogWarning("Transform returned empty text");
            _dictationBar.ShowError("Transform failed");
            return;
        }
        
        if (transformedText.Trim() == selectedText.Trim())
        {
            _logger.LogWarning("Transform returned same text as input");
            _dictationBar.ShowError("No changes made");
            return;
        }

        _logger.LogInformation("Transformed text: {Len} chars", transformedText.Length);

        // Replace the selected text with the transformed text
        _dictationBar.Hide();
        await ClipboardHelper.ReplaceSelectedTextAsync(transformedText);
        _logger.LogInformation("Replaced selected text");
    }

    private async Task ProcessTextGenerateAsync(string instruction, CancellationToken cancellationToken)
    {
        _dictationBar.ShowPolishing("Generating...");
        _logger.LogInformation("Generating text with instruction: '{Instruction}'", instruction);

        var settings = _settingsManager.CurrentSettings;
        _commandModeService ??= _serviceFactory.CreatePolishService(settings.CommandModeModelId);

        if (!_commandModeService.IsReady)
        {
            _logger.LogInformation("Initializing command mode service...");
            await _commandModeService.InitializeAsync(cancellationToken);
        }

        string generatedText;
        try
        {
            // Pass screenshot for multimodal context if available (Groq Llama 4 supports images)
            generatedText = await _commandModeService.GenerateAsync(instruction, _currentScreenshot, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Generate failed - polish service not ready");
            _dictationBar.ShowError("Polish model failed. Use OpenAI in settings.");
            return;
        }

        _logger.LogDebug("GenerateAsync returned: '{Text}', ImageUsed: {HasImage}", 
            string.IsNullOrEmpty(generatedText) ? "(empty)" : 
            (generatedText.Length > 100 ? generatedText[..100] + "..." : generatedText),
            _currentScreenshot != null);

        if (string.IsNullOrWhiteSpace(generatedText))
        {
            _logger.LogWarning("Generate returned empty text");
            _dictationBar.ShowError("Generation failed");
            return;
        }

        _logger.LogInformation("Generated text: {Len} chars", generatedText.Length);

        // Insert the generated text at cursor position
        _dictationBar.Hide();
        await ClipboardHelper.ReplaceSelectedTextAsync(generatedText);
        _logger.LogInformation("Inserted generated text");
    }

    // ===== Code Dictation Mode Handlers =====

    private void OnCodeDictationRecordStart(object? sender, EventArgs e)
    {
        if (!_isEnabled || _isProcessing) return;
        if (_servicesInitializing)
        {
            _dictationBar.ShowError("Models loading...");
            return;
        }

        try
        {
            _currentOperationCts = new CancellationTokenSource();
            
            // Start recording and show overlay
            _dictationBar.ShowRecording("Code Dictation");
            _audioRecorder.StartRecording();
            _logger.LogInformation("Code dictation recording started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start code dictation recording");
            _dictationBar.ShowError("Failed to start recording");
        }
    }

    private async void OnCodeDictationRecordStop(object? sender, EventArgs e)
    {
        // Multiple guards to prevent double-processing
        if (!_audioRecorder.IsRecording) return;
        if (_isProcessing) return;
        
        _isProcessing = true;

        var audioFilePath = _audioRecorder.StopRecording();
        if (string.IsNullOrEmpty(audioFilePath))
        {
            _dictationBar.Hide();
            _isProcessing = false;
            return;
        }

        await ProcessCodeDictationAsync(audioFilePath);
    }

    private async Task ProcessCodeDictationAsync(string audioFilePath)
    {
        // _isProcessing already set in OnCodeDictationRecordStop
        var settings = _settingsManager.CurrentSettings;
        var cts = _currentOperationCts;

        try
        {
            _transcriptionService ??= _serviceFactory.CreateTranscriptionService(settings.TranscriptionModelId);
            _codeDictationService ??= _serviceFactory.CreateCodeDictationService(settings.CodeDictationModelId);

            // Initialize transcription if needed
            if (!_transcriptionService.IsReady)
            {
                _dictationBar.ShowTranscribing("Loading model...");
                await _transcriptionService.InitializeAsync(cts?.Token ?? CancellationToken.None);
            }

            _dictationBar.ShowTranscribing("Converting speech...");
            _logger.LogInformation("Processing code dictation with {Model}...", _transcriptionService.ModelId);

            // Transcribe the spoken code
            var rawTranscript = await _transcriptionService.TranscribeAsync(
                audioFilePath,
                settings.Language == "auto" ? null : settings.Language,
                cts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(rawTranscript))
            {
                _dictationBar.ShowError("No speech detected");
                return;
            }

            _logger.LogInformation("Raw transcript: {Transcript}", rawTranscript);

            // Initialize code dictation service if needed
            if (!_codeDictationService.IsReady)
            {
                _dictationBar.ShowPolishing("Loading code model...");
                await _codeDictationService.InitializeAsync(cts?.Token ?? CancellationToken.None);
            }

            // Convert to code
            _dictationBar.ShowPolishing("Generating code...");
            var code = await _codeDictationService.ConvertToCodeAsync(
                rawTranscript.Trim(),
                settings.CodeDictationLanguage,
                cts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(code))
            {
                _logger.LogWarning("Code conversion returned empty");
                _dictationBar.ShowError("Code conversion failed");
                return;
            }

            _logger.LogInformation("Generated code ({Len} chars): {Code}", code.Length, 
                code.Length > 100 ? code[..100] + "..." : code);

            // Inject the generated code
            _dictationBar.Hide();
            await _textInjector.InjectTextAsync(code, cts?.Token ?? CancellationToken.None);
            _logger.LogInformation("Injected code: {Len} chars", code.Length);
        }
        catch (OperationCanceledException)
        {
            _dictationBar.Hide();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Code dictation failed: {Msg}", ex.Message);
            _dictationBar.ShowError(ex.Message.Length > 60 ? ex.Message[..57] + "..." : ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Code dictation error: {Msg}", ex.Message);
            _dictationBar.ShowError("Error: " + (ex.Message.Length > 50 ? ex.Message[..47] + "..." : ex.Message));
        }
        finally
        {
            _isProcessing = false;
            _audioRecorder.DeleteTempFile(audioFilePath);
            if (cts == _currentOperationCts) _currentOperationCts = null;
            cts?.Dispose();
        }
    }
}
