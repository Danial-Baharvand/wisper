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
    private InteractiveExecutor? _executor;  // Keep executor warm between calls
    private bool _isInitialized;
    private bool _initializationFailed;

    public string ModelId => _model.Id;
    public bool IsReady => _isInitialized && _executor != null;

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
                _executor = new InteractiveExecutor(_context);  // Create executor once at load
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
        
        var resultMatch = Regex.Match(output, @"<result>\s*(.*?)\s*(?:</result>|$)", RegexOptions.Singleline);
        if (resultMatch.Success && !string.IsNullOrWhiteSpace(resultMatch.Groups[1].Value))
        {
            output = resultMatch.Groups[1].Value.Trim();
        }
        
        output = Regex.Replace(output, @"<[^>]+>", "").Trim();
        
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
        
        output = output.Trim();
        if ((output.StartsWith("\"") && output.EndsWith("\"")) || 
            (output.StartsWith("'") && output.EndsWith("'")))
            output = output[1..^1];
            
        return string.IsNullOrWhiteSpace(output) ? originalText : output.Trim();
    }

    public async Task<string> PolishAsync(string rawText, bool notesMode = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return rawText;
        if (_executor == null || _context == null) return rawText;

        _logger.LogInformation("Polishing with {Model}", _model.Name);
        var startTime = DateTime.UtcNow;
        
        try
        {
            var prompt = BuildPrompt(rawText);
            var inferenceParams = new InferenceParams 
            { 
                MaxTokens = 200, 
                Temperature = 0.1f, 
                AntiPrompts = GetStopTokens()
            };

            // Run inference completely off the UI thread to prevent freezing
            var resultText = await Task.Run(async () =>
            {
                // Clear KV cache for fresh inference each time
                _context.NativeHandle.KvCacheClear();
                
                var result = new StringBuilder();
                await foreach (var token in _executor.InferAsync(prompt, inferenceParams, cancellationToken))
                {
                    result.Append(token);
                    if (result.Length > rawText.Length * 3) break;
                }
                return result.ToString();
            }, cancellationToken);
            
            var polished = ExtractResult(resultText, rawText);
            
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
        _executor = null;
        _context?.Dispose();
        _weights?.Dispose();
        _context = null;
        _weights = null;
        _isInitialized = false;
    }
}
