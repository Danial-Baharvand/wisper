using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WisperFlow.Services.CodeContext;

namespace WisperFlow.Services.Polish;

/// <summary>
/// Polish service using Cerebras Cloud API.
/// Cerebras offers ultra-fast inference (2000+ tokens/sec) with OpenAI-compatible API.
/// </summary>
public class CerebrasPolishService : IPolishService
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly string _apiModelName;
    private readonly string? _customTypingPrompt;
    private readonly string? _customNotesPrompt;
    private readonly CodeContextService? _codeContextService;
    private const string Endpoint = "https://api.cerebras.ai/v1/chat/completions";

    public string ModelId => _modelId;
    public bool IsReady => !string.IsNullOrEmpty(GetApiKey());
    
    /// <summary>
    /// Gets the effective typing prompt (custom or default).
    /// </summary>
    public string TypingPrompt => string.IsNullOrWhiteSpace(_customTypingPrompt) 
        ? OpenAIPolishService.DefaultTypingPrompt 
        : _customTypingPrompt;
    
    /// <summary>
    /// Gets the effective notes prompt (custom or default).
    /// </summary>
    public string NotesPrompt => string.IsNullOrWhiteSpace(_customNotesPrompt) 
        ? OpenAIPolishService.DefaultNotesPrompt 
        : _customNotesPrompt;

    public CerebrasPolishService(ILogger logger, string modelId, string? customTypingPrompt = null, string? customNotesPrompt = null, CodeContextService? codeContextService = null)
    {
        _logger = logger;
        _modelId = modelId;
        _apiModelName = GetApiModelName(modelId);
        _customTypingPrompt = customTypingPrompt;
        _customNotesPrompt = customNotesPrompt;
        _codeContextService = codeContextService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }
    
    private static string GetApiModelName(string modelId) => modelId switch
    {
        "cerebras-llama-3.3-70b" => "llama-3.3-70b",
        "cerebras-llama3.1-8b" => "llama3.1-8b",
        "cerebras-gpt-oss-120b" => "gpt-oss-120b",
        "cerebras-qwen-3-32b" => "qwen-3-32b",
        "cerebras-qwen-3-235b-a22b" => "qwen-3-235b-a22b-instruct-2507",
        "cerebras-zai-glm-4.6" => "zai-glm-4.6",
        _ => "llama-3.3-70b"  // Default to flagship model
    };

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<string> PolishAsync(string rawText, bool notesMode = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return rawText;

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No Cerebras API key, returning raw text");
            return rawText;
        }

        _logger.LogInformation("Polishing via Cerebras {Model} ({Mode} mode)", _apiModelName, notesMode ? "notes" : "typing");

        try
        {
            // Get code context if available
            var codeContext = await GetCodeContextAsync();
            var systemPrompt = (notesMode ? NotesPrompt : TypingPrompt) + (codeContext ?? "");
            
            // Use higher max_tokens when code context is included to prevent truncation
            var maxTokens = codeContext != null ? 1200 : 600;
            
            var requestBody = new Dictionary<string, object>
            {
                ["model"] = _apiModelName,
                ["messages"] = new[]
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt },
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = rawText }
                },
                ["max_tokens"] = maxTokens,
                ["temperature"] = 0.1
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
                _logger.LogWarning("Cerebras polish API failed ({Code}): {Error}", (int)response.StatusCode, errorBody);
                return rawText;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            
            // Get the full response and check for finish reason
            var choice = doc.RootElement.GetProperty("choices")[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "unknown";
            var result = choice
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? rawText;

            result = result.Trim().Trim('"');
            _logger.LogInformation("Cerebras polish complete: {Len} chars, finish: {Reason}", result.Length, finishReason);
            
            // Log warning if output seems truncated
            if (result.Length < rawText.Length && rawText.Length > 50)
            {
                _logger.LogWarning("Polish output ({OutputLen}) shorter than input ({InputLen}). First 100 chars: {Preview}", 
                    result.Length, rawText.Length, 
                    result.Length > 100 ? result.Substring(0, 100) + "..." : result);
            }
            
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cerebras polish failed, returning raw text");
            return rawText;
        }
    }
    
    /// <summary>
    /// Gets code context from the active editor if available.
    /// </summary>
    private async Task<string?> GetCodeContextAsync()
    {
        if (_codeContextService == null)
            return null;
        
        try
        {
            var context = await _codeContextService.GetContextForPromptAsync();
            if (context != null)
            {
                var contextString = context.ToPromptString();
                _logger.LogInformation("Including code context: {Files} files, {Symbols} symbols", 
                    context.FileNames.Count, context.Symbols.Count);
                return contextString;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get code context");
        }
        
        return null;
    }

    public async Task<string> TransformAsync(string originalText, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originalText)) return originalText;
        if (string.IsNullOrWhiteSpace(command)) return originalText;

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No Cerebras API key for transform");
            return originalText;
        }

        _logger.LogInformation("Transforming via Cerebras {Model}: {Command}", _apiModelName, command);

        var systemPrompt = @"You are a text transformation assistant. Transform the provided text according to the user's instruction.

RULES:
1. Apply the instruction to transform the text
2. Keep the core meaning and information
3. Return ONLY the transformed text, no explanations
4. If the instruction is unclear, make a reasonable interpretation";

        try
        {
            var requestBody = new Dictionary<string, object>
            {
                ["model"] = _apiModelName,
                ["messages"] = new[]
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt },
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = $"Instruction: {command}\n\nText to transform:\n{originalText}" }
                },
                ["max_tokens"] = 1000,
                ["temperature"] = 0.3
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
                _logger.LogWarning("Cerebras transform API failed ({Code}): {Error}", (int)response.StatusCode, errorBody);
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
            _logger.LogInformation("Cerebras transform complete: {Len} chars", result.Length);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cerebras transform failed");
            return originalText;
        }
    }

    public async Task<string> GenerateAsync(string instruction, byte[]? imageBytes = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return "";
        // Note: imageBytes is accepted but not used - Cerebras does not support multimodal

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No Cerebras API key for generate");
            return "";
        }

        _logger.LogInformation("Generating via Cerebras {Model}: {Instruction}", _apiModelName, instruction);

        var systemPrompt = @"You are a helpful writing assistant. Generate text exactly as the user requests.
Output only the requested text with no explanations, preamble, or commentary.";

        try
        {
            var requestBody = new Dictionary<string, object>
            {
                ["model"] = _apiModelName,
                ["messages"] = new[]
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt },
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = instruction }
                },
                ["max_tokens"] = 1500,
                ["temperature"] = 0.7
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
                _logger.LogWarning("Cerebras generate API failed ({Code}): {Error}", (int)response.StatusCode, errorBody);
                return "";
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            var result = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            result = result.Trim();
            _logger.LogInformation("Cerebras generate complete: {Len} chars", result.Length);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cerebras generate failed");
            return "";
        }
    }

    private static string? GetApiKey() =>
        Environment.GetEnvironmentVariable("CEREBRAS_API_KEY") ?? CredentialManager.GetCerebrasApiKey();

    public void Dispose() => _httpClient.Dispose();
}
