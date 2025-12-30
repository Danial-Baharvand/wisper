using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services.CodeDictation;

/// <summary>
/// OpenAI-based code dictation service.
/// Converts natural language to code using GPT models.
/// </summary>
public class OpenAICodeDictationService : ICodeDictationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAICodeDictationService> _logger;
    private readonly string _apiModelName;
    private readonly string? _customPrompt;
    
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    /// <summary>
    /// Default prompt for Python code dictation.
    /// </summary>
    public const string DefaultPythonPrompt = @"You are a voice-to-code converter. Convert natural language dictation to Python code.

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

    public string ModelId { get; }
    public bool IsReady => true; // Always ready if API key exists

    public OpenAICodeDictationService(
        ModelInfo model,
        ILogger<OpenAICodeDictationService> logger,
        string? customPrompt = null)
    {
        ModelId = model.Id;
        _logger = logger;
        _customPrompt = customPrompt;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        
        // Map our model IDs to OpenAI model names
        _apiModelName = model.Id switch
        {
            "openai-gpt5-mini" => "gpt-4.1-mini",
            "openai-gpt4o-mini" => "gpt-4o-mini",
            _ => "gpt-4o-mini"
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
            _logger.LogWarning("No API key for code dictation");
            throw new InvalidOperationException("OpenAI API key not configured. Please add it in Settings.");
        }

        _logger.LogInformation("Converting to {Language} via OpenAI {Model}: {Input}", 
            language, _apiModelName, naturalLanguage);

        var systemPrompt = GetSystemPrompt(language, _customPrompt);

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
                max_completion_tokens = 512
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
                _logger.LogWarning("Code API failed ({Code}): {Error}", (int)response.StatusCode, errorBody);
                throw new InvalidOperationException($"OpenAI API error: {response.StatusCode}");
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
            
            _logger.LogInformation("Code conversion complete: {Len} chars", code.Length);
            return code;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            _logger.LogWarning(ex, "Code conversion failed");
            return "";
        }
    }

    private static string GetSystemPrompt(string language, string? customPrompt)
    {
        // Use custom prompt if provided (for Python)
        if (language.ToLowerInvariant() == "python" && !string.IsNullOrWhiteSpace(customPrompt))
        {
            return customPrompt;
        }
        
        if (language.ToLowerInvariant() == "python")
        {
            return DefaultPythonPrompt;
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
        var key = CredentialManager.GetApiKey();
        if (!string.IsNullOrEmpty(key)) return key;
        
        // Fall back to environment variable
        return Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

