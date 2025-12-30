using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using WisperFlow.Services.CodeDictation;
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

    public ServiceFactory(ILoggerFactory loggerFactory, ModelManager modelManager, SettingsManager settingsManager)
    {
        _loggerFactory = loggerFactory;
        _modelManager = modelManager;
        _settingsManager = settingsManager;
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
                customNotesPrompt);
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

    public ICodeDictationService CreateCodeDictationService(string modelId)
    {
        var model = ModelCatalog.GetById(modelId);
        
        if (model == null || model.Source == ModelSource.OpenAI)
        {
            // Use OpenAI API for code dictation
            var apiModel = model ?? ModelCatalog.OpenAIGpt4oMini;
            return new OpenAICodeDictationService(
                apiModel,
                _loggerFactory.CreateLogger<OpenAICodeDictationService>());
        }

        // Use local LLM for code dictation
        return new LocalCodeDictationService(
            _modelManager,
            model,
            _loggerFactory.CreateLogger<LocalCodeDictationService>());
    }
}
