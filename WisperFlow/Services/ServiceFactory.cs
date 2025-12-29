using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using WisperFlow.Services.Polish;
using WisperFlow.Services.Transcription;

namespace WisperFlow.Services;

/// <summary>
/// Factory for creating transcription and polish services based on selected models.
/// </summary>
public class ServiceFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ModelManager _modelManager;

    public ServiceFactory(ILoggerFactory loggerFactory, ModelManager modelManager)
    {
        _loggerFactory = loggerFactory;
        _modelManager = modelManager;
    }

    public ITranscriptionService CreateTranscriptionService(string modelId)
    {
        var model = ModelCatalog.GetById(modelId);
        if (model == null || model.Type != ModelType.Whisper || model.Source == ModelSource.OpenAI)
        {
            return new OpenAITranscriptionService(_loggerFactory.CreateLogger<OpenAITranscriptionService>());
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
        
        if (model == null || model.Source == ModelSource.OpenAI)
        {
            return new OpenAIPolishService(_loggerFactory.CreateLogger<OpenAIPolishService>());
        }

        if (model.Id == "polish-disabled")
        {
            return new DisabledPolishService();
        }

        return new LocalLLMPolishService(
            _loggerFactory.CreateLogger<LocalLLMPolishService>(),
            _modelManager,
            model);
    }
}

