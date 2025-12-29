using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
                    ContextSize = 512,
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

    private string BuildPrompt(string text)
    {
        // Use XML tags for structured output - easier to parse
        var instruction = "Fix punctuation/capitalization. Remove filler words (um, uh, like). Output ONLY the result inside <result></result> tags.";
        
        return _model.PromptTemplate switch
        {
            "gemma" => $"<start_of_turn>user\n{instruction}\n\nInput: {text}<end_of_turn>\n<start_of_turn>model\n<result>",
            "mistral" => $"[INST] {instruction}\n\nInput: {text} [/INST]\n<result>",
            "openchat" => $"GPT4 Correct User: {instruction}\n\nInput: {text}<|end_of_turn|>GPT4 Correct Assistant: <result>",
            "tinyllama" => $"<|user|>\n{instruction}\n\nInput: {text}</s>\n<|assistant|>\n<result>",
            _ => $"### Instruction:\n{instruction}\n\n### Input:\n{text}\n\n### Response:\n<result>"
        };
    }

    private string[] GetStopTokens()
    {
        // Always include </result> as primary stop token
        return _model.PromptTemplate switch
        {
            "gemma" => new[] { "</result>", "<end_of_turn>", "<start_of_turn>" },
            "mistral" => new[] { "</result>", "</s>", "[INST]" },
            "openchat" => new[] { "</result>", "<|end_of_turn|>" },
            "tinyllama" => new[] { "</result>", "</s>", "<|user|>" },
            _ => new[] { "</result>", "\n\n\n", "###" }
        };
    }

    private string ExtractResult(string output, string originalText)
    {
        if (string.IsNullOrWhiteSpace(output)) return originalText;
        
        // Try to extract content from <result> tags
        var resultMatch = Regex.Match(output, @"<result>\s*(.*?)\s*(?:</result>|$)", RegexOptions.Singleline);
        if (resultMatch.Success && !string.IsNullOrWhiteSpace(resultMatch.Groups[1].Value))
        {
            output = resultMatch.Groups[1].Value.Trim();
        }
        
        // Remove any remaining XML tags
        output = Regex.Replace(output, @"<[^>]+>", "").Trim();
        
        // Remove common preambles
        var preambles = new[] {
            "Sure, here is", "Here is the", "Here's the", "The corrected", "Corrected:",
            "Output:", "Result:", "Fixed:", "Cleaned:"
        };
        foreach (var p in preambles)
        {
            var idx = output.IndexOf(p, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < 30)
            {
                var colonIdx = output.IndexOf(':', idx);
                if (colonIdx > 0 && colonIdx < idx + 50)
                    output = output[(colonIdx + 1)..].TrimStart();
            }
        }
        
        // Remove quotes if wrapped
        output = output.Trim();
        if ((output.StartsWith("\"") && output.EndsWith("\"")) || 
            (output.StartsWith("'") && output.EndsWith("'")))
            output = output[1..^1];
            
        return string.IsNullOrWhiteSpace(output) ? originalText : output.Trim();
    }

    public async Task<string> PolishAsync(string rawText, bool notesMode = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return rawText;
        if (_context == null || _weights == null) return rawText;

        _logger.LogInformation("Polishing with {Model}", _model.Name);
        var startTime = DateTime.UtcNow;
        
        try
        {
            var prompt = BuildPrompt(rawText);
            var executor = new StatelessExecutor(_weights, _context.Params);
            var inferenceParams = new InferenceParams 
            { 
                MaxTokens = 200, 
                Temperature = 0.1f, 
                AntiPrompts = GetStopTokens()
            };

            var result = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
            {
                result.Append(token);
                if (result.Length > rawText.Length * 3) break;
            }
            
            var polished = ExtractResult(result.ToString(), rawText);
            
            var elapsed = DateTime.UtcNow - startTime;
            _logger.LogInformation("Polish done in {Time:F1}s", elapsed.TotalSeconds);
            
            return polished;
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
