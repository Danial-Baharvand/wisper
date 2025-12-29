using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

/// <summary>
/// Polishes/cleans transcribed text using OpenAI's text models.
/// Handles punctuation, capitalization, formatting commands, and grammar.
/// </summary>
public class TextPolisher
{
    private readonly ILogger<TextPolisher> _logger;
    private readonly HttpClient _httpClient;
    private const string ChatCompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    
    // Use a cheap, fast model for text cleanup
    private const string Model = "gpt-4o-mini";
    private const int MaxOutputTokens = 600;

    // System prompts for different modes
    private const string TypingModePrompt = @"You are a transcription post-processor. Clean up the raw speech-to-text output with MINIMAL changes:

RULES:
1. Add proper punctuation (periods, commas, question marks)
2. Fix capitalization (sentence starts, proper nouns)
3. Remove filler words: ""um"", ""uh"", ""like"" (when used as filler), ""you know""
4. Keep the user's exact wording and intent
5. Preserve numbers, emails, URLs, and technical terms exactly

FORMATTING COMMANDS (convert these spoken phrases):
- ""new line"" or ""newline"" → insert actual newline
- ""new paragraph"" → insert double newline
- ""comma"" → ,
- ""period"" or ""full stop"" → .
- ""question mark"" → ?
- ""exclamation point"" or ""exclamation mark"" → !
- ""colon"" → :
- ""semicolon"" → ;
- ""open parenthesis"" / ""close parenthesis"" → ( / )
- ""open quote"" / ""close quote"" or ""quote"" / ""end quote"" → "" / ""
- ""bullet point"" or ""bullet"" followed by text → • [text]

OUTPUT: Return ONLY the cleaned text. No quotes, no markdown, no explanations.";

    private const string NotesModePrompt = @"You are a transcription post-processor optimizing for note-taking. Clean up speech-to-text output:

RULES:
1. Add proper punctuation and capitalization
2. Remove ALL filler words (um, uh, like, you know, basically, actually, so, well)
3. Clean up run-on sentences into clear, concise statements
4. Fix obvious grammar issues
5. Preserve technical terms, numbers, emails, URLs exactly
6. Make the text scannable and readable

FORMATTING COMMANDS (convert these spoken phrases):
- ""new line"" → newline
- ""new paragraph"" → double newline
- ""bullet point"" or ""bullet"" + text → • [text]
- ""heading"" + text → **[text]** (bold heading)
- ""subheading"" + text → *[text]* (italic subheading)
- ""number one/two/three"" at start of items → 1. 2. 3.
- Punctuation commands (comma, period, etc.) → actual punctuation

STRUCTURE:
- If user speaks multiple bullet points, format as a bulleted list
- If user speaks numbered items, format as a numbered list
- If user says ""heading: [something]"", make it a heading

OUTPUT: Return ONLY the cleaned and formatted text. No explanations, no wrapper quotes.";

    public TextPolisher(ILogger<TextPolisher> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Polishes the raw transcript text.
    /// </summary>
    /// <param name="rawText">Raw transcription from Whisper.</param>
    /// <param name="notesMode">If true, uses more aggressive cleanup for note-taking.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Polished text.</returns>
    public async Task<string> PolishAsync(
        string rawText,
        bool notesMode = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return rawText;
        }

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No API key found, returning raw text without polishing");
            return rawText;
        }

        var systemPrompt = notesMode ? NotesModePrompt : TypingModePrompt;

        _logger.LogInformation("Polishing text ({Mode} mode), input length: {Length} chars",
            notesMode ? "notes" : "typing", rawText.Length);

        try
        {
            var requestBody = new
            {
                model = Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = rawText }
                },
                max_tokens = MaxOutputTokens,
                temperature = 0.1 // Low temperature for consistent output
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Polish request failed with {StatusCode}: {Error}. Returning raw text.",
                    (int)response.StatusCode, errorBody);
                return rawText;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            
            var polishedText = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? rawText;

            // Clean up any wrapper quotes the model might have added despite instructions
            polishedText = polishedText.Trim();
            if (polishedText.StartsWith('"') && polishedText.EndsWith('"'))
            {
                polishedText = polishedText[1..^1];
            }

            _logger.LogInformation("Polish successful, output length: {Length} chars", polishedText.Length);
            return polishedText;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Polish operation cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polish failed, returning raw text");
            return rawText;
        }
    }

    private string? GetApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return envKey;
        }

        return CredentialManager.GetApiKey();
    }
}

