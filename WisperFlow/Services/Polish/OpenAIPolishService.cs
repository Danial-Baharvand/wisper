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

    public string ModelId => "openai-gpt4o-mini";
    public bool IsReady => !string.IsNullOrEmpty(GetApiKey());

    public OpenAIPolishService(ILogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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

        _logger.LogInformation("Polishing via OpenAI ({Mode} mode)", notesMode ? "notes" : "typing");

        try
        {
            var requestBody = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "system", content = notesMode ? NotesPrompt : TypingPrompt },
                    new { role = "user", content = rawText }
                },
                max_tokens = 600,
                temperature = 0.1
            };

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

    private static string? GetApiKey() =>
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? CredentialManager.GetApiKey();

    public void Dispose() => _httpClient.Dispose();
}

