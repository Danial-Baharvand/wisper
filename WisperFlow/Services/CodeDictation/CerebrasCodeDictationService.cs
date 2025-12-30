using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services.CodeDictation;

/// <summary>
/// Cerebras-based code dictation service.
/// Converts natural language to code using Cerebras Cloud API (ultra-fast inference).
/// </summary>
public class CerebrasCodeDictationService : ICodeDictationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CerebrasCodeDictationService> _logger;
    private readonly string _apiModelName;
    
    private const string Endpoint = "https://api.cerebras.ai/v1/chat/completions";

    public string ModelId { get; }
    public bool IsReady => true; // Always ready if API key exists

    public CerebrasCodeDictationService(
        ModelInfo model,
        ILogger<CerebrasCodeDictationService> logger)
    {
        ModelId = model.Id;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        
        // Map our model IDs to Cerebras API model names
        _apiModelName = model.Id switch
        {
            "cerebras-llama-3.3-70b" => "llama-3.3-70b",
            "cerebras-llama3.1-8b" => "llama3.1-8b",
            "cerebras-gpt-oss-120b" => "gpt-oss-120b",
            "cerebras-qwen-3-32b" => "qwen-3-32b",
            "cerebras-qwen-3-235b-a22b" => "qwen-3-235b-a22b-instruct-2507",
            "cerebras-zai-glm-4.6" => "zai-glm-4.6",
            _ => "llama-3.3-70b"  // Default to flagship model
        };
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // No initialization needed for API
        return Task.CompletedTask;
    }

    public async Task<string> ConvertToCodeAsync(
        string naturalLanguage, 
        string language, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguage))
            return "";

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No Cerebras API key for code dictation");
            throw new InvalidOperationException("Cerebras API key not configured. Please add it in Settings.");
        }

        _logger.LogInformation("Converting to {Language} via Cerebras {Model}: {Input}", 
            language, _apiModelName, naturalLanguage);

        var systemPrompt = GetSystemPrompt(language);

        try
        {
            var requestBody = new
            {
                model = _apiModelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = naturalLanguage }
                },
                max_tokens = 512,
                temperature = 0.2
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Cerebras code API failed ({Code}): {Error}", (int)response.StatusCode, errorBody);
                throw new InvalidOperationException($"Cerebras API error: {response.StatusCode}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            var result = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            // Extract code from markdown if present
            var code = ExtractCode(result.Trim(), language);
            
            _logger.LogInformation("Cerebras code conversion complete: {Len} chars", code.Length);
            return code;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            _logger.LogWarning(ex, "Cerebras code conversion failed");
            return "";
        }
    }

    private static string GetSystemPrompt(string language)
    {
        if (language.ToLowerInvariant() == "python")
        {
            return @"You are a voice-to-code converter. Convert natural language dictation to Python code.

RULES:
1. Output ONLY valid Python code - no explanations, no markdown code fences, no comments unless requested.
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
4. Handle compound statements properly with correct indentation.
5. Variable naming: Use snake_case for variables.
6. Recognize numbers: ""one"" → 1, ""ten"" → 10, ""hundred"" → 100.
7. Boolean values: ""true"" → True, ""false"" → False, ""none"" → None.
8. List operations: ""append x to list"" → list.append(x).
9. String quotes: Use double quotes for strings.

OUTPUT: Only the Python code, nothing else. No markdown, no explanations.";
        }
        
        return $"Convert natural language dictation to {language} code. Output only valid code, no markdown or explanations.";
    }

    private static string ExtractCode(string output, string language)
    {
        // Remove markdown code fences if present
        if (output.StartsWith($"```{language}"))
            output = output[$"```{language}".Length..];
        else if (output.StartsWith("```python"))
            output = output["```python".Length..];
        else if (output.StartsWith("```"))
            output = output[3..];

        var endFence = output.IndexOf("```");
        if (endFence >= 0)
            output = output[..endFence];

        return output.Trim();
    }

    private static string? GetApiKey()
    {
        // Try Credential Manager first
        var key = CredentialManager.GetCerebrasApiKey();
        if (!string.IsNullOrEmpty(key)) return key;
        
        // Fall back to environment variable
        return Environment.GetEnvironmentVariable("CEREBRAS_API_KEY");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
