using Microsoft.Extensions.Logging;
using WisperFlow.Models;
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
    private readonly OverlayWindow _overlayWindow;
    private readonly SettingsManager _settingsManager;
    private readonly ServiceFactory _serviceFactory;
    private readonly ILogger<DictationOrchestrator> _logger;

    private ITranscriptionService? _transcriptionService;
    private IPolishService? _polishService;
    private ICodeDictationService? _codeDictationService;
    private CancellationTokenSource? _currentOperationCts;
    private bool _isEnabled = true;
    private bool _isProcessing;
    private bool _servicesInitializing;
    private bool _isCommandMode;  // Track if we're processing a command
    private bool _isCodeDictationMode;  // Track if we're processing code dictation
    private string? _commandModeSelectedText;  // Selected text captured at command stop
    private string? _commandModeSearchContext;  // Highlighted text for search context (when not in textbox)
    private bool _commandModeTextInputFocused;  // Whether a text input was focused

    public DictationOrchestrator(
        HotkeyManager hotkeyManager,
        AudioRecorder audioRecorder,
        TextInjector textInjector,
        OverlayWindow overlayWindow,
        SettingsManager settingsManager,
        ServiceFactory serviceFactory,
        ILogger<DictationOrchestrator> logger)
    {
        _hotkeyManager = hotkeyManager;
        _audioRecorder = audioRecorder;
        _textInjector = textInjector;
        _overlayWindow = overlayWindow;
        _settingsManager = settingsManager;
        _serviceFactory = serviceFactory;
        _logger = logger;

        _hotkeyManager.RecordStart += OnRecordStart;
        _hotkeyManager.RecordStop += OnRecordStop;
        _hotkeyManager.CommandRecordStart += OnCommandRecordStart;
        _hotkeyManager.CommandRecordStop += OnCommandRecordStop;
        _hotkeyManager.CodeDictationRecordStart += OnCodeDictationRecordStart;
        _hotkeyManager.CodeDictationRecordStop += OnCodeDictationRecordStop;
        _audioRecorder.MaxDurationReached += OnMaxDurationReached;
        _audioRecorder.RecordingProgress += OnRecordingProgress;
    }

    public void ApplySettings(AppSettings settings)
    {
        _isEnabled = settings.HotkeyEnabled;
        _audioRecorder.SetMaxDuration(settings.MaxRecordingDurationSeconds);

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

        _logger.LogInformation("Settings applied: Enabled={Enabled}, Polish={Polish}, CommandMode={CommandMode}, CodeDictation={CodeDictation}", 
            settings.HotkeyEnabled, settings.PolishOutput, settings.CommandModeEnabled, settings.CodeDictationEnabled);
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
            _overlayWindow.Hide();
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
            _overlayWindow.ShowError("Models loading...");
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
        if (!_audioRecorder.IsRecording) return;

        var audioFilePath = _audioRecorder.StopRecording();
        if (string.IsNullOrEmpty(audioFilePath))
        {
            _overlayWindow.Hide();
            return;
        }

        await ProcessRecordingAsync(audioFilePath);
    }

    private void OnMaxDurationReached(object? sender, EventArgs e) => _overlayWindow.ShowError("Max duration reached");
    private void OnRecordingProgress(object? sender, TimeSpan duration) => _overlayWindow.UpdateRecordingTime(duration);

    private async Task ProcessRecordingAsync(string audioFilePath)
    {
        _isProcessing = true;
        var settings = _settingsManager.CurrentSettings;
        var cts = _currentOperationCts;

        try
        {
            _transcriptionService ??= _serviceFactory.CreateTranscriptionService(settings.TranscriptionModelId);
            _polishService ??= _serviceFactory.CreatePolishService(settings.PolishModelId);

            // Initialize if needed
            if (!_transcriptionService.IsReady)
            {
                _overlayWindow.ShowTranscribing("Loading model...");
                await _transcriptionService.InitializeAsync(cts?.Token ?? CancellationToken.None);
            }

            _overlayWindow.ShowTranscribing();
            _logger.LogInformation("Processing with {Model}...", _transcriptionService.ModelId);

            var transcript = await _transcriptionService.TranscribeAsync(
                audioFilePath,
                settings.Language == "auto" ? null : settings.Language,
                cts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                _overlayWindow.ShowError("No speech detected");
                return;
            }

            _logger.LogDebug("Transcript: {Len} chars", transcript.Length);

            var finalText = transcript;
            if (settings.PolishOutput && _polishService.ModelId != "polish-disabled")
            {
                if (!_polishService.IsReady)
                {
                    _overlayWindow.ShowPolishing("Loading model...");
                    await _polishService.InitializeAsync(cts?.Token ?? CancellationToken.None);
                }

                _overlayWindow.ShowPolishing();
                finalText = await _polishService.PolishAsync(transcript, settings.NotesMode, cts?.Token ?? CancellationToken.None);
                _logger.LogDebug("Polished: {Len} chars", finalText.Length);
            }

            _overlayWindow.Hide();
            await _textInjector.InjectTextAsync(finalText, cts?.Token ?? CancellationToken.None);
            _logger.LogInformation("Injected {Len} chars", finalText.Length);
        }
        catch (OperationCanceledException)
        {
            _overlayWindow.Hide();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Dictation failed: {Msg}", ex.Message);
            _overlayWindow.ShowError(ex.Message.Length > 60 ? ex.Message[..57] + "..." : ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dictation error: {Msg}", ex.Message);
            _overlayWindow.ShowError("Error: " + (ex.Message.Length > 50 ? ex.Message[..47] + "..." : ex.Message));
        }
        finally
        {
            _isProcessing = false;
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
            _overlayWindow.ShowError("Models loading...");
            return;
        }

        try
        {
            _currentOperationCts = new CancellationTokenSource();
            _isCommandMode = true;
            _commandModeSelectedText = null;  // Will be captured on stop
            
            // Start recording and show overlay IMMEDIATELY
            _overlayWindow.ShowRecording("Command Mode");
            _audioRecorder.StartRecording();
            _logger.LogInformation("Command recording started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start command recording");
            _overlayWindow.ShowError("Failed to start recording");
            _isCommandMode = false;
            _commandModeSelectedText = null;
        }
    }

    private async void OnCommandRecordStop(object? sender, EventArgs e)
    {
        if (!_audioRecorder.IsRecording) return;

        var audioFilePath = _audioRecorder.StopRecording();
        if (string.IsNullOrEmpty(audioFilePath))
        {
            _overlayWindow.Hide();
            _isCommandMode = false;
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
        _isProcessing = true;
        var settings = _settingsManager.CurrentSettings;
        var cts = _currentOperationCts;

        try
        {
            _transcriptionService ??= _serviceFactory.CreateTranscriptionService(settings.TranscriptionModelId);
            _polishService ??= _serviceFactory.CreatePolishService(settings.PolishModelId);

            // Initialize transcription if needed
            if (!_transcriptionService.IsReady)
            {
                _overlayWindow.ShowTranscribing("Loading model...");
                await _transcriptionService.InitializeAsync(cts?.Token ?? CancellationToken.None);
            }

            _overlayWindow.ShowTranscribing("Processing command...");
            _logger.LogInformation("Processing command with {Model}...", _transcriptionService.ModelId);

            // Transcribe the spoken command
            var rawCommand = await _transcriptionService.TranscribeAsync(
                audioFilePath,
                settings.Language == "auto" ? null : settings.Language,
                cts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(rawCommand))
            {
                _overlayWindow.ShowError("No command detected");
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
                
                _overlayWindow.Hide();
                await BrowserQueryService.OpenQueryAsync(searchQuery, settings.CommandModeSearchEngine);
                _logger.LogInformation("Opened query in {Service}: {Query}", settings.CommandModeSearchEngine, 
                    searchQuery.Length > 50 ? searchQuery[..50] + "..." : searchQuery);
            }
        }
        catch (OperationCanceledException)
        {
            _overlayWindow.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command processing error: {Msg}", ex.Message);
            _overlayWindow.ShowError("Error: " + (ex.Message.Length > 50 ? ex.Message[..47] + "..." : ex.Message));
        }
        finally
        {
            _isProcessing = false;
            _isCommandMode = false;
            _commandModeSelectedText = null;  // Clear captured text
            _commandModeSearchContext = null;  // Clear search context
            _audioRecorder.DeleteTempFile(audioFilePath);
            if (cts == _currentOperationCts) _currentOperationCts = null;
            cts?.Dispose();
        }
    }

    private async Task ProcessTextTransformAsync(string selectedText, string command, CancellationToken cancellationToken)
    {
        _overlayWindow.ShowPolishing("Transforming...");
        _logger.LogInformation("Transforming {Len} chars with command: '{Command}'", selectedText.Length, command);
        _logger.LogDebug("Original text: '{Text}'", selectedText.Length > 100 ? selectedText[..100] + "..." : selectedText);

        var settings = _settingsManager.CurrentSettings;
        _polishService ??= _serviceFactory.CreatePolishService(settings.PolishModelId);

        if (!_polishService.IsReady)
        {
            _logger.LogInformation("Initializing polish service...");
            await _polishService.InitializeAsync(cancellationToken);
        }

        _logger.LogDebug("Calling TransformAsync...");
        string transformedText;
        try
        {
            transformedText = await _polishService.TransformAsync(selectedText, command, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Transform failed - polish service not ready");
            _overlayWindow.ShowError("Polish model failed. Use OpenAI in settings.");
            return;
        }
        
        _logger.LogDebug("TransformAsync returned: '{Text}'", 
            string.IsNullOrEmpty(transformedText) ? "(empty)" : 
            (transformedText.Length > 100 ? transformedText[..100] + "..." : transformedText));

        if (string.IsNullOrWhiteSpace(transformedText))
        {
            _logger.LogWarning("Transform returned empty text");
            _overlayWindow.ShowError("Transform failed");
            return;
        }
        
        if (transformedText.Trim() == selectedText.Trim())
        {
            _logger.LogWarning("Transform returned same text as input");
            _overlayWindow.ShowError("No changes made");
            return;
        }

        _logger.LogInformation("Transformed text: {Len} chars", transformedText.Length);

        // Replace the selected text with the transformed text
        _overlayWindow.Hide();
        await ClipboardHelper.ReplaceSelectedTextAsync(transformedText);
        _logger.LogInformation("Replaced selected text");
    }

    private async Task ProcessTextGenerateAsync(string instruction, CancellationToken cancellationToken)
    {
        _overlayWindow.ShowPolishing("Generating...");
        _logger.LogInformation("Generating text with instruction: '{Instruction}'", instruction);

        var settings = _settingsManager.CurrentSettings;
        _polishService ??= _serviceFactory.CreatePolishService(settings.PolishModelId);

        if (!_polishService.IsReady)
        {
            _logger.LogInformation("Initializing polish service...");
            await _polishService.InitializeAsync(cancellationToken);
        }

        string generatedText;
        try
        {
            generatedText = await _polishService.GenerateAsync(instruction, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Generate failed - polish service not ready");
            _overlayWindow.ShowError("Polish model failed. Use OpenAI in settings.");
            return;
        }

        _logger.LogDebug("GenerateAsync returned: '{Text}'", 
            string.IsNullOrEmpty(generatedText) ? "(empty)" : 
            (generatedText.Length > 100 ? generatedText[..100] + "..." : generatedText));

        if (string.IsNullOrWhiteSpace(generatedText))
        {
            _logger.LogWarning("Generate returned empty text");
            _overlayWindow.ShowError("Generation failed");
            return;
        }

        _logger.LogInformation("Generated text: {Len} chars", generatedText.Length);

        // Insert the generated text at cursor position
        _overlayWindow.Hide();
        await ClipboardHelper.ReplaceSelectedTextAsync(generatedText);
        _logger.LogInformation("Inserted generated text");
    }

    // ===== Code Dictation Mode Handlers =====

    private void OnCodeDictationRecordStart(object? sender, EventArgs e)
    {
        if (!_isEnabled || _isProcessing) return;
        if (_servicesInitializing)
        {
            _overlayWindow.ShowError("Models loading...");
            return;
        }

        try
        {
            _currentOperationCts = new CancellationTokenSource();
            _isCodeDictationMode = true;
            
            // Start recording and show overlay
            _overlayWindow.ShowRecording("Code Dictation");
            _audioRecorder.StartRecording();
            _logger.LogInformation("Code dictation recording started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start code dictation recording");
            _overlayWindow.ShowError("Failed to start recording");
            _isCodeDictationMode = false;
        }
    }

    private async void OnCodeDictationRecordStop(object? sender, EventArgs e)
    {
        if (!_audioRecorder.IsRecording) return;

        var audioFilePath = _audioRecorder.StopRecording();
        if (string.IsNullOrEmpty(audioFilePath))
        {
            _overlayWindow.Hide();
            _isCodeDictationMode = false;
            return;
        }

        await ProcessCodeDictationAsync(audioFilePath);
    }

    private async Task ProcessCodeDictationAsync(string audioFilePath)
    {
        _isProcessing = true;
        var settings = _settingsManager.CurrentSettings;
        var cts = _currentOperationCts;

        try
        {
            _transcriptionService ??= _serviceFactory.CreateTranscriptionService(settings.TranscriptionModelId);
            _codeDictationService ??= _serviceFactory.CreateCodeDictationService(settings.CodeDictationModelId);

            // Initialize transcription if needed
            if (!_transcriptionService.IsReady)
            {
                _overlayWindow.ShowTranscribing("Loading model...");
                await _transcriptionService.InitializeAsync(cts?.Token ?? CancellationToken.None);
            }

            _overlayWindow.ShowTranscribing("Converting speech...");
            _logger.LogInformation("Processing code dictation with {Model}...", _transcriptionService.ModelId);

            // Transcribe the spoken code
            var rawTranscript = await _transcriptionService.TranscribeAsync(
                audioFilePath,
                settings.Language == "auto" ? null : settings.Language,
                cts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(rawTranscript))
            {
                _overlayWindow.ShowError("No speech detected");
                return;
            }

            _logger.LogInformation("Raw transcript: {Transcript}", rawTranscript);

            // Initialize code dictation service if needed
            if (!_codeDictationService.IsReady)
            {
                _overlayWindow.ShowPolishing("Loading code model...");
                await _codeDictationService.InitializeAsync(cts?.Token ?? CancellationToken.None);
            }

            // Convert to code
            _overlayWindow.ShowPolishing("Generating code...");
            var code = await _codeDictationService.ConvertToCodeAsync(
                rawTranscript.Trim(),
                settings.CodeDictationLanguage,
                cts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(code))
            {
                _logger.LogWarning("Code conversion returned empty");
                _overlayWindow.ShowError("Code conversion failed");
                return;
            }

            _logger.LogInformation("Generated code ({Len} chars): {Code}", code.Length, 
                code.Length > 100 ? code[..100] + "..." : code);

            // Inject the generated code
            _overlayWindow.Hide();
            await _textInjector.InjectTextAsync(code, cts?.Token ?? CancellationToken.None);
            _logger.LogInformation("Injected code: {Len} chars", code.Length);
        }
        catch (OperationCanceledException)
        {
            _overlayWindow.Hide();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Code dictation failed: {Msg}", ex.Message);
            _overlayWindow.ShowError(ex.Message.Length > 60 ? ex.Message[..57] + "..." : ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Code dictation error: {Msg}", ex.Message);
            _overlayWindow.ShowError("Error: " + (ex.Message.Length > 50 ? ex.Message[..47] + "..." : ex.Message));
        }
        finally
        {
            _isProcessing = false;
            _isCodeDictationMode = false;
            _audioRecorder.DeleteTempFile(audioFilePath);
            if (cts == _currentOperationCts) _currentOperationCts = null;
            cts?.Dispose();
        }
    }
}
