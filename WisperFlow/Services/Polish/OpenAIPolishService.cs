using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WisperFlow.Services.CodeContext;

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
    private readonly string? _customTypingPrompt;
    private readonly string? _customNotesPrompt;
    private readonly CodeContextService? _codeContextService;
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    /// <summary>
    /// Default typing mode prompt - used when no custom prompt is set.
    /// </summary>
    public const string DefaultTypingPrompt = @"You are a speech-to-text post-processor. Clean up the transcription intelligently.

SELF-CORRECTIONS (remove the incorrect part, keep the correction):
- ""I went to the oh actually I drove to the store"" → ""I drove to the store""
- ""The meeting is on Monday no wait Tuesday"" → ""The meeting is on Tuesday""
- ""She said hello sorry she said goodbye"" → ""She said goodbye""
- ""discard last sentence"" / ""delete that"" / ""scratch that"" → remove the preceding sentence
- ""let's go back to the beginning"" / ""start over"" → keep only what follows

INLINE EDITS (apply the requested change):
- ""change the word good to great"" → replace 'good' with 'great' in the text
- ""replace happy with excited"" → replace 'happy' with 'excited' in the text

STUTTERING & DUPLICATES:
- ""I I I went to the store"" → ""I went to the store""
- ""the the meeting"" → ""the meeting""

FORMATTING COMMANDS:
- ""new line"" / ""next line"" → actual newline
- ""new paragraph"" → double newline
- ""open parenthesis"" / ""open paren"" → (
- ""close parenthesis"" / ""close paren"" → )
- ""open bracket"" → [, ""close bracket"" → ]
- ""open quote"" / ""quote"" → "", ""close quote"" / ""end quote"" / ""unquote"" → ""
- ""comma"" → ,  ""period"" / ""full stop"" → .  ""question mark"" → ?
- ""colon"" → :  ""semicolon"" → ;  ""dash"" / ""hyphen"" → -
- ""exclamation point"" / ""exclamation mark"" → !

ALSO:
- Fix punctuation and capitalization
- Remove filler words (um, uh, like, you know, basically, so, I mean)
- Fix homophones (there/their/they're, your/you're)
- Fix phonetic mistranscriptions (""my crow soft"" → ""Microsoft"")

Return ONLY the cleaned text, nothing else.";

    /// <summary>
    /// Default notes mode prompt - used when no custom prompt is set.
    /// </summary>
    public const string DefaultNotesPrompt = @"You are a speech-to-text post-processor for note-taking. Format the transcription as clean notes.

SELF-CORRECTIONS (remove the incorrect part, keep the correction):
- ""The price is fifty oh actually sixty dollars"" → ""The price is sixty dollars""
- ""We need three no make that four items"" → ""We need four items""
- ""discard last sentence"" / ""delete that"" → remove the preceding sentence

INLINE EDITS:
- ""change X to Y"" / ""replace X with Y"" → apply the substitution

FORMATTING COMMANDS:
- ""bullet point"" + text → • text
- ""numbered list"" → 1. 2. 3. format
- ""heading"" + text → **text**
- ""new line"" → newline, ""new paragraph"" → double newline
- Parentheses, brackets, quotes → actual symbols

CLEANUP:
- Remove ALL filler words and hesitations
- Remove stuttering and repeated words
- Fix grammar and homophones
- Fix phonetic mistranscriptions

Return ONLY the formatted text, nothing else.";

    public string ModelId => _modelId;
    public bool IsReady => !string.IsNullOrEmpty(GetApiKey());
    
    /// <summary>
    /// Gets the effective typing prompt (custom or default).
    /// </summary>
    public string TypingPrompt => string.IsNullOrWhiteSpace(_customTypingPrompt) ? DefaultTypingPrompt : _customTypingPrompt;
    
    /// <summary>
    /// Gets the effective notes prompt (custom or default).
    /// </summary>
    public string NotesPrompt => string.IsNullOrWhiteSpace(_customNotesPrompt) ? DefaultNotesPrompt : _customNotesPrompt;

    public OpenAIPolishService(ILogger logger, string modelId = "openai-gpt4o-mini", string? customTypingPrompt = null, string? customNotesPrompt = null, CodeContextService? codeContextService = null)
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
            // Get code context if available
            var codeContext = await GetCodeContextAsync();
            var systemPrompt = (notesMode ? NotesPrompt : TypingPrompt) + (codeContext ?? "");
            
            // Use higher max_tokens when code context is included
            var maxTokens = codeContext != null ? 1200 : 600;
            
            var requestBody = BuildRequestBody(
                systemPrompt,
                rawText,
                maxTokens: maxTokens,
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
            
            var choice = doc.RootElement.GetProperty("choices")[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "unknown";
            var result = choice
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? rawText;

            result = result.Trim().Trim('"');
            _logger.LogInformation("Polish complete: {Len} chars, finish: {Reason}", result.Length, finishReason);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Polish failed, returning raw text");
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

    public async Task<string> GenerateAsync(string instruction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return "";

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No API key for generate");
            return "";
        }

        _logger.LogInformation("Generating via OpenAI {Model}: {Instruction}", _apiModelName, instruction);

        var systemPrompt = @"You are a helpful writing assistant. Generate text exactly as the user requests.
Output only the requested text with no explanations, preamble, or commentary.";

        try
        {
            var requestBody = BuildRequestBody(
                systemPrompt,
                instruction,
                maxTokens: 1500,
                temperature: 0.7  // More creative for generation
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
                _logger.LogWarning("Generate API failed ({Code}): {Error}", (int)response.StatusCode, errorBody);
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
            _logger.LogInformation("Generate complete: {Len} chars", result.Length);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Generate failed");
            return "";
        }
    }

    private static string? GetApiKey() =>
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? CredentialManager.GetApiKey();

    public void Dispose() => _httpClient.Dispose();
}

