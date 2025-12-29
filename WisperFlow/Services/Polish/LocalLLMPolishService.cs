using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using LLama;
using LLama.Common;
using WisperFlow.Models;

namespace WisperFlow.Services.Polish;

public class LocalLLMPolishService : IPolishService
{
    private readonly ILogger _logger;
    private readonly ModelManager _modelManager;
    private readonly ModelInfo _model;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private bool _isInitialized;
    private bool _initializationFailed;

    private const string TypingPrompt = "<|system|>\nClean up this transcription. Fix punctuation and capitalization. Remove filler words. Output ONLY the cleaned text.</s>\n<|user|>\n{0}</s>\n<|assistant|>\n";
    private const string NotesPrompt = "<|system|>\nFormat as clean notes. Fix punctuation, remove filler words. Output ONLY the text.</s>\n<|user|>\n{0}</s>\n<|assistant|>\n";

    public string ModelId => _model.Id;
    public bool IsReady => _isInitialized && _context != null;

    public LocalLLMPolishService(ILogger logger, ModelManager modelManager, ModelInfo model)
    {
        _logger = logger;
        _modelManager = modelManager;
        _model = model;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized || _initializationFailed) return;
        var modelPath = _modelManager.GetModelPath(_model);
        if (!File.Exists(modelPath))
            throw new InvalidOperationException($"Model {_model.Name} not downloaded.");

        _logger.LogInformation("Loading LLM: {Model} ({Size})", _model.Name, _model.SizeFormatted);
        try
        {
            await Task.Run(() =>
            {
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = 1024,
                    GpuLayerCount = 0,
                    Threads = (uint)Math.Max(1, Environment.ProcessorCount / 2)
                };
                _weights = LLamaWeights.LoadFromFile(parameters);
                _context = _weights.CreateContext(parameters);
            }, cancellationToken);
            _isInitialized = true;
            _logger.LogInformation("LLM loaded successfully");
        }
        catch (Exception ex)
        {
            _initializationFailed = true;
            _logger.LogError(ex, "Failed to load LLM");
            throw new InvalidOperationException($"Failed to load LLM: {ex.Message}", ex);
        }
    }

    public async Task<string> PolishAsync(string rawText, bool notesMode = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return rawText;
        if (_context == null || _weights == null) return rawText;

        _logger.LogInformation("Polishing with {Model}", _model.Name);
        try
        {
            var prompt = string.Format(notesMode ? NotesPrompt : TypingPrompt, rawText);
            var executor = new StatelessExecutor(_weights, _context.Params);
            var inferenceParams = new InferenceParams { MaxTokens = 256, Temperature = 0.1f, AntiPrompts = new[] { "</s>", "<|user|>" } };

            var result = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
            {
                result.Append(token);
                if (result.Length > rawText.Length * 2) break;
            }
            var polished = result.ToString().Trim();
            var endIdx = polished.IndexOf("</s>");
            if (endIdx > 0) polished = polished[..endIdx].Trim();
            return string.IsNullOrWhiteSpace(polished) ? rawText : polished;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Polish failed");
            return rawText;
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _weights?.Dispose();
        _context = null;
        _weights = null;
        _isInitialized = false;
    }
}
