using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services.CodeDictation;

/// <summary>
/// Groq-based code dictation service.
/// Converts natural language to code using Groq Cloud API (ultra-fast LPU inference).
/// </summary>
public class GroqCodeDictationService : ICodeDictationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GroqCodeDictationService> _logger;
    private readonly string _apiModelName;
    private readonly string? _customPrompt;
    
    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";

    public string ModelId { get; }
    public bool IsReady => true; // Always ready if API key exists

    public GroqCodeDictationService(
        ModelInfo model,
        ILogger<GroqCodeDictationService> logger,
        string? customPrompt = null)
    {
        ModelId = model.Id;
        _logger = logger;
        _customPrompt = customPrompt;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        
        // Map our model IDs to Groq API model names
        _apiModelName = model.Id switch
        {
            "groq-llama-4-scout" => "meta-llama/llama-4-scout-17b-16e-instruct",
            "groq-llama-4-maverick" => "meta-llama/llama-4-maverick-17b-128e-instruct",
            _ => "meta-llama/llama-4-scout-17b-16e-instruct"  // Default to Scout
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
            _logger.LogWarning("No Groq API key for code dictation");
            throw new InvalidOperationException("Groq API key not configured. Set GROQ_API_KEY environment variable.");
        }

        _logger.LogInformation("Converting to {Language} via Groq {Model}: {Input}", 
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
                _logger.LogWarning("Groq code API failed ({Code}): {Error}", (int)response.StatusCode, errorBody);
                throw new InvalidOperationException($"Groq API error: {response.StatusCode}");
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
            
            _logger.LogInformation("Groq code conversion complete: {Len} chars", code.Length);
            return code;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            _logger.LogWarning(ex, "Groq code conversion failed");
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
            // Use the shared default prompt from OpenAICodeDictationService
            return OpenAICodeDictationService.DefaultPythonPrompt;
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
        var key = CredentialManager.GetGroqApiKey();
        if (!string.IsNullOrEmpty(key)) return key;
        
        // Fall back to environment variable
        return Environment.GetEnvironmentVariable("GROQ_API_KEY");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
