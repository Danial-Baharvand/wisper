using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

public class OpenAITranscriptionClient
{
    private readonly ILogger<OpenAITranscriptionClient> _logger;
    private readonly HttpClient _httpClient;
    private const string TranscriptionEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    private const int MaxRetries = 3;
    private const int MaxFileSizeMB = 25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OpenAITranscriptionClient(ILogger<OpenAITranscriptionClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task<string> TranscribeAsync(
        string audioFilePath,
        string? language = null,
        string? customPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("No API key found");
            throw new InvalidOperationException("No API key found. Configure in Settings or set OPENAI_API_KEY env var.");
        }

        var keyPreview = apiKey.Length > 8 ? apiKey[..8] + "..." : "***";
        _logger.LogDebug("Using API key: {KeyPreview}", keyPreview);

        var fileInfo = new FileInfo(audioFilePath);
        var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);
        if (fileSizeMB > MaxFileSizeMB)
            throw new InvalidOperationException($"Recording too large ({fileSizeMB:F1}MB). Max is {MaxFileSizeMB}MB.");

        _logger.LogInformation("Transcribing audio file, size: {SizeMB:F2}MB", fileSizeMB);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var content = new MultipartFormDataContent();

                var fileBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(fileContent, "file", Path.GetFileName(audioFilePath));
                content.Add(new StringContent("whisper-1"), "model");

                if (!string.IsNullOrWhiteSpace(language) && language.ToLower() != "auto")
                    content.Add(new StringContent(language), "language");

                content.Add(new StringContent("0"), "temperature");
                content.Add(new StringContent("json"), "response_format");

                if (!string.IsNullOrWhiteSpace(customPrompt))
                    content.Add(new StringContent(customPrompt), "prompt");

                using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = content;

                _logger.LogDebug("Sending transcription request (attempt {Attempt}/{MaxRetries})", attempt, MaxRetries);

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogDebug("API Response: {Response}", responseJson);

                    var result = JsonSerializer.Deserialize<TranscriptionResponse>(responseJson, JsonOptions);
                    var text = result?.Text ?? string.Empty;
                    
                    _logger.LogInformation("Transcription successful, length: {Length} chars", text.Length);
                    return text;
                }

                var statusCode = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("API Error {StatusCode}: {Error}", statusCode, errorBody);

                if (statusCode == 429 || statusCode >= 500)
                {
                    var delay = (int)Math.Pow(2, attempt - 1) * 1000;
                    _logger.LogWarning("Retrying in {Delay}ms...", delay);
                    await Task.Delay(delay, cancellationToken);
                    lastException = new HttpRequestException($"API returned {statusCode}");
                    continue;
                }

                var msg = statusCode switch
                {
                    401 => "Invalid API key. Check your OpenAI API key in Settings.",
                    403 => "API access denied. Check your OpenAI account.",
                    _ => $"API error ({statusCode}): {errorBody}"
                };
                throw new InvalidOperationException(msg);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                if (attempt == MaxRetries) break;
                await Task.Delay(1000 * attempt, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Transcription failed after {MaxRetries} attempts", lastException);
    }

    private string? GetApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey;
        return CredentialManager.GetApiKey();
    }

    private class TranscriptionResponse
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }
}