using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services.Polish;

/// <summary>
/// Polish service using OpenAI's GPT models.
/// </summary>
public class OpenAIPolishService : IPolishService
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly string _apiModelName;
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    private const string TypingPrompt = @"Clean up this speech transcription with minimal changes:
- Add punctuation and fix capitalization
- Remove filler words (um, uh, like, you know)
- Convert spoken commands: ""new line"" → newline, ""comma"" → ,
Return ONLY the cleaned text, nothing else.";

    private const string NotesPrompt = @"Format this speech transcription as clean notes:
- Add punctuation and capitalization
- Remove ALL filler words
- Convert: ""bullet point"" → •, ""heading"" → **text**
Return ONLY the formatted text, nothing else.";

    public string ModelId => _modelId;
    public bool IsReady => !string.IsNullOrEmpty(GetApiKey());

    public OpenAIPolishService(ILogger logger, string modelId = "openai-gpt4o-mini")
    {
        _logger = logger;
        _modelId = modelId;
        _apiModelName = GetApiModelName(modelId);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }
    
    private static string GetApiModelName(string modelId) => modelId switch
    {
        "openai-gpt5-nano" => "gpt-5-nano",
        "openai-gpt5-mini" => "gpt-5-mini",
        "openai-gpt4o-mini" => "gpt-4o-mini",
        _ => "gpt-4o-mini"
    };
    
    private bool IsGpt5Model => _apiModelName.StartsWith("gpt-5");
    
    private Dictionary<string, object> BuildRequestBody(string systemPrompt, string userContent, int maxTokens, double temperature)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _apiModelName,
            ["messages"] = new[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = userContent }
            }
        };
        
        // GPT-5 models use max_completion_tokens and don't support custom temperature
        if (IsGpt5Model)
        {
            body["max_completion_tokens"] = maxTokens;
            // GPT-5 only supports temperature=1 (default), so we don't set it
        }
        else
        {
            body["max_tokens"] = maxTokens;
            body["temperature"] = temperature;
        }
            
        return body;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<string> PolishAsync(string rawText, bool notesMode = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return rawText;

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No API key, returning raw text");
            return rawText;
        }

        _logger.LogInformation("Polishing via OpenAI {Model} ({Mode} mode)", _apiModelName, notesMode ? "notes" : "typing");

        try
        {
            var requestBody = BuildRequestBody(
                notesMode ? NotesPrompt : TypingPrompt,
                rawText,
                maxTokens: 600,
                temperature: 0.1
            );

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Polish API failed ({Code}), returning raw text", (int)response.StatusCode);
                return rawText;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            var result = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? rawText;

            result = result.Trim().Trim('"');
            _logger.LogInformation("Polish complete: {Len} chars", result.Length);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Polish failed, returning raw text");
            return rawText;
        }
    }

    public async Task<string> TransformAsync(string originalText, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originalText)) return originalText;
        if (string.IsNullOrWhiteSpace(command)) return originalText;

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No API key for transform");
            return originalText;
        }

        _logger.LogInformation("Transforming via OpenAI {Model}: {Command}", _apiModelName, command);

        var systemPrompt = @"You are a text transformation assistant. Transform the provided text according to the user's instruction.

RULES:
1. Apply the instruction to transform the text
2. Keep the core meaning and information
3. Return ONLY the transformed text, no explanations
4. If the instruction is unclear, make a reasonable interpretation";

        try
        {
            var requestBody = BuildRequestBody(
                systemPrompt,
                $"Instruction: {command}\n\nText to transform:\n{originalText}",
                maxTokens: 1000,
                temperature: 0.3
            );

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Transform API failed ({Code}): {Error}", (int)response.StatusCode, errorBody);
                return originalText;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            var result = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? originalText;

            result = result.Trim();
            _logger.LogInformation("Transform complete: {Len} chars", result.Length);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Transform failed");
            return originalText;
        }
    }

    private static string? GetApiKey() =>
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? CredentialManager.GetApiKey();

    public void Dispose() => _httpClient.Dispose();
}

