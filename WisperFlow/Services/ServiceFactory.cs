using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using WisperFlow.Services.CodeContext;
using WisperFlow.Services.Polish;
using WisperFlow.Services.Transcription;

namespace WisperFlow.Services;

/// <summary>
/// Factory for creating transcription, polish, and code dictation services based on selected models.
/// </summary>
public class ServiceFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ModelManager _modelManager;
    private readonly SettingsManager _settingsManager;
    private readonly CodeContextService? _codeContextService;

    public ServiceFactory(ILoggerFactory loggerFactory, ModelManager modelManager, SettingsManager settingsManager, CodeContextService? codeContextService = null)
    {
        _loggerFactory = loggerFactory;
        _modelManager = modelManager;
        _settingsManager = settingsManager;
        _codeContextService = codeContextService;
    }

    public ITranscriptionService CreateTranscriptionService(string modelId)
    {
        var model = ModelCatalog.GetById(modelId);
        if (model == null || model.Type != ModelType.Whisper || model.Source == ModelSource.OpenAI)
        {
            return new OpenAITranscriptionService(_loggerFactory.CreateLogger<OpenAITranscriptionService>());
        }

        // Use Deepgram for Deepgram models
        if (model.Source == ModelSource.Deepgram)
        {
            return new DeepgramTranscriptionService(
                _loggerFactory.CreateLogger<DeepgramTranscriptionService>(),
                model,
                _settingsManager.CurrentSettings);
        }

        // Use faster-whisper for FasterWhisper models (Python sidecar)
        if (model.Source == ModelSource.FasterWhisper)
        {
            return new FasterWhisperService(
                _loggerFactory.CreateLogger<FasterWhisperService>(),
                model);
        }

        // Use Groq for Groq Whisper models
        if (model.Source == ModelSource.Groq)
        {
            return new GroqTranscriptionService(
                _loggerFactory.CreateLogger<GroqTranscriptionService>(),
                model);
        }

        return new LocalWhisperService(
            _loggerFactory.CreateLogger<LocalWhisperService>(),
            _modelManager,
            model);
    }

    public IPolishService CreatePolishService(string modelId)
    {
        var model = ModelCatalog.GetById(modelId);
        var settings = _settingsManager.CurrentSettings;
        var customTypingPrompt = string.IsNullOrWhiteSpace(settings.CustomTypingPrompt) ? null : settings.CustomTypingPrompt;
        var customNotesPrompt = string.IsNullOrWhiteSpace(settings.CustomNotesPrompt) ? null : settings.CustomNotesPrompt;
        
        if (model == null || model.Source == ModelSource.OpenAI)
        {
            return new OpenAIPolishService(
                _loggerFactory.CreateLogger<OpenAIPolishService>(), 
                modelId,
                customTypingPrompt,
                customNotesPrompt,
                _codeContextService);
        }

        if (model.Source == ModelSource.Cerebras)
        {
            return new CerebrasPolishService(
                _loggerFactory.CreateLogger<CerebrasPolishService>(),
                modelId,
                customTypingPrompt,
                customNotesPrompt,
                _codeContextService);
        }

        if (model.Source == ModelSource.Groq)
        {
            return new GroqPolishService(
                _loggerFactory.CreateLogger<GroqPolishService>(),
                modelId,
                customTypingPrompt,
                customNotesPrompt,
                _codeContextService);
        }

        if (model.Id == "polish-disabled")
        {
            return new DisabledPolishService();
        }

        return new LocalLLMPolishService(
            _loggerFactory.CreateLogger<LocalLLMPolishService>(),
            _modelManager,
            model,
            customTypingPrompt,
            customNotesPrompt);
    }
}
