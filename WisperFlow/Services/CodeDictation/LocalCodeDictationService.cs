using System.IO;
using System.Text;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services.CodeDictation;

/// <summary>
/// Local LLM-based code dictation service using LLamaSharp.
/// Converts natural language speech to Python code.
/// </summary>
public class LocalCodeDictationService : ICodeDictationService
{
    private readonly ModelManager _modelManager;
    private readonly ModelInfo _model;
    private readonly ILogger<LocalCodeDictationService> _logger;
    
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private bool _isReady;
    
    public string ModelId => _model.Id;
    public bool IsReady => _isReady;

    public LocalCodeDictationService(
        ModelManager modelManager,
        ModelInfo model,
        ILogger<LocalCodeDictationService> logger)
    {
        _modelManager = modelManager;
        _model = model;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isReady) return;

        var modelPath = _modelManager.GetModelPath(_model);
        
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException($"Model not downloaded: {_model.Name}. Please download it from Model Manager.");
        }

        _logger.LogInformation("Loading LLM for code dictation: {Model} ({Size})", 
            _model.Name, _model.SizeFormatted);

        await Task.Run(() =>
        {
            var parameters = new ModelParams(modelPath)
            {
                ContextSize = 4096,  // Larger context for code
                GpuLayerCount = 0,   // CPU only for compatibility
                Threads = Math.Max(1, Environment.ProcessorCount / 2),
                UseMemorymap = true
            };
            
            _weights = LLamaWeights.LoadFromFile(parameters);
            _context = _weights.CreateContext(parameters);
        }, cancellationToken);

        _isReady = true;
        _logger.LogInformation("Code dictation LLM loaded: {Model}", _model.Name);
    }

    public async Task<string> ConvertToCodeAsync(
        string naturalLanguage, 
        string language, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguage))
            return "";

        if (!_isReady || _weights == null)
        {
            throw new InvalidOperationException("Code dictation LLM not loaded. Please initialize first.");
        }

        _logger.LogInformation("Converting to {Language}: {Input}", language, naturalLanguage);
        var startTime = DateTime.UtcNow;

        try
        {
            var prompt = BuildPrompt(naturalLanguage, language);
            
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 512,
                AntiPrompts = GetStopTokens(),
                SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
                {
                    Temperature = _model.Temperature
                }
            };

            var resultText = await Task.Run(async () =>
            {
                var modelParams = new ModelParams(_modelManager.GetModelPath(_model))
                {
                    ContextSize = 4096,
                    GpuLayerCount = 0,
                    Threads = Math.Max(1, Environment.ProcessorCount / 2),
                    UseMemorymap = true
                };
                var executor = new StatelessExecutor(_weights!, modelParams);

                var result = new StringBuilder();
                await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
                {
                    result.Append(token);
                    // Safety limit
                    if (result.Length > 2000) break;
                }
                return result.ToString();
            }, cancellationToken);

            _logger.LogDebug("Raw code output ({Len} chars): {Output}", 
                resultText.Length, resultText.Length > 200 ? resultText[..200] + "..." : resultText);

            var code = ExtractCode(resultText, language);
            
            var elapsed = DateTime.UtcNow - startTime;
            _logger.LogInformation("Code conversion done in {Time:F1}s: {Len} chars", 
                elapsed.TotalSeconds, code.Length);

            return code;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Code conversion failed");
            return "";
        }
    }

    private string BuildPrompt(string input, string language)
    {
        var systemPrompt = GetSystemPrompt(language);
        
        return _model.PromptTemplate switch
        {
            "qwen2" => $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n{input}<|im_end|>\n<|im_start|>assistant\n```{language}\n",
            "qwen3" => $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n/no_think\n{input}<|im_end|>\n<|im_start|>assistant\n```{language}\n",
            "llama3" => $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n{systemPrompt}<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n{input}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n```{language}\n",
            "gemma2" or "gemma3" => $"<start_of_turn>user\n{systemPrompt}\n\nConvert: {input}<end_of_turn>\n<start_of_turn>model\n```{language}\n",
            "mistral" => $"[INST] {systemPrompt}\n\nConvert: {input} [/INST]```{language}\n",
            "openchat" => $"GPT4 Correct User: {systemPrompt}\n\nConvert: {input}<|end_of_turn|>GPT4 Correct Assistant: ```{language}\n",
            _ => $"### System:\n{systemPrompt}\n\n### User:\n{input}\n\n### Assistant:\n```{language}\n"
        };
    }

    private static string GetSystemPrompt(string language)
    {
        if (language.ToLowerInvariant() == "python")
        {
            return @"You are a voice-to-code converter. Convert natural language dictation to Python code.

RULES:
1. Output ONLY valid Python code - no explanations, no markdown, no comments unless requested.
2. Use 4-space indentation for Python.
3. Convert spoken words to proper syntax:
   - ""for i in range n"" → for i in range(n):
   - ""for i in range 10"" → for i in range(10):
   - ""my variable equals 5"" → my_variable = 5
   - ""counter plus equals 1"" → counter += 1
   - ""if x greater than y"" → if x > y:
   - ""if x is not equal to y"" → if x != y:
   - ""while true"" → while True:
   - ""define function foo"" → def foo():
   - ""define function add that takes a and b"" → def add(a, b):
   - ""class person"" → class Person:
   - ""import numpy as np"" → import numpy as np
   - ""from collections import defaultdict"" → from collections import defaultdict
   - ""return x"" → return x
   - ""print hello world"" → print(""hello world"")
   - ""comment this is a test"" → # this is a test
   - ""docstring this function calculates sum"" → """"""This function calculates sum.""""""
4. Handle compound statements:
   - ""for item in my list colon print item"" → for item in my_list:\n    print(item)
5. Variable naming: Use snake_case for variables (""my variable"" → my_variable).
6. Recognize numbers: ""one"" → 1, ""ten"" → 10, ""hundred"" → 100.
7. Boolean values: ""true"" → True, ""false"" → False, ""none"" → None.
8. List operations: ""append x to list"" → list.append(x).
9. String quotes: Use double quotes for strings.
10. Operators: ""plus""/""add"" → +, ""minus""/""subtract"" → -, ""times""/""multiply"" → *, ""divided by"" → /, ""modulo""/""mod"" → %.

OUTPUT: Only the Python code, nothing else.";
        }
        
        // Default for other languages (future)
        return $"Convert natural language dictation to {language} code. Output only valid code.";
    }

    private List<string> GetStopTokens()
    {
        var stops = new List<string> { "```", "\n\n\n", "<|im_end|>", "<|eot_id|>", "<end_of_turn>" };
        
        return _model.PromptTemplate switch
        {
            "qwen2" or "qwen3" => new List<string> { "```", "<|im_end|>", "<|im_start|>", "\n\n\n" },
            "llama3" => new List<string> { "```", "<|eot_id|>", "<|end_of_text|>", "\n\n\n" },
            "gemma2" or "gemma3" => new List<string> { "```", "<end_of_turn>", "<start_of_turn>", "\n\n\n" },
            "mistral" => new List<string> { "```", "</s>", "[INST]", "\n\n\n" },
            "openchat" => new List<string> { "```", "<|end_of_turn|>", "GPT4 Correct", "\n\n\n" },
            _ => stops
        };
    }

    private string ExtractCode(string rawOutput, string language)
    {
        var output = rawOutput.Trim();
        
        // Remove any leading/trailing code fence markers
        if (output.StartsWith($"```{language}"))
            output = output[$"```{language}".Length..];
        else if (output.StartsWith("```"))
            output = output[3..];
        
        // Find end of code block
        var endFence = output.IndexOf("```");
        if (endFence >= 0)
            output = output[..endFence];
        
        // Remove any model-specific tokens
        var cleanTokens = new[] { "<|im_end|>", "<|eot_id|>", "<end_of_turn>", "</s>", "<|end_of_turn|>" };
        foreach (var token in cleanTokens)
        {
            var idx = output.IndexOf(token);
            if (idx >= 0)
                output = output[..idx];
        }
        
        // Clean up whitespace
        output = output.Trim();
        
        // Ensure proper line endings
        output = output.Replace("\r\n", "\n").Replace("\r", "\n");
        
        return output;
    }

    public void Dispose()
    {
        _context?.Dispose();
        _weights?.Dispose();
        _context = null;
        _weights = null;
        _isReady = false;
        GC.SuppressFinalize(this);
    }
}

