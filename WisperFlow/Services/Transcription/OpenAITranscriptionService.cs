using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services.Transcription;

/// <summary>
/// Transcription service using OpenAI's Whisper API.
/// </summary>
public class OpenAITranscriptionService : ITranscriptionService
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private const string Endpoint = "https://api.openai.com/v1/audio/transcriptions";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string ModelId => "openai-whisper";
    public bool IsReady => !string.IsNullOrEmpty(GetApiKey());

    public OpenAITranscriptionService(ILogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null, 
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No API key found. Configure in Settings.");

        var fileInfo = new FileInfo(audioFilePath);
        _logger.LogInformation("Transcribing via OpenAI API, size: {Size:F2}MB", fileInfo.Length / 1_000_000.0);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var content = new MultipartFormDataContent();
                
                // Use streaming upload instead of loading entire file into memory
                using var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, 
                    FileShare.Read, bufferSize: 4096, useAsync: true);
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(fileContent, "file", Path.GetFileName(audioFilePath));
                content.Add(new StringContent("whisper-1"), "model");
                
                if (!string.IsNullOrWhiteSpace(language) && language.ToLower() != "auto")
                    content.Add(new StringContent(language), "language");
                
                content.Add(new StringContent("json"), "response_format");

                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = content;

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var result = JsonSerializer.Deserialize<TranscriptionResponse>(json, JsonOptions);
                    var transcriptText = result?.Text ?? "";
                    _logger.LogInformation("Transcription successful: {Len} chars", transcriptText.Length);
                    _logger.LogInformation("Transcript: {Transcript}", transcriptText);
                    return transcriptText;
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode == 429 || statusCode >= 500)
                {
                    await Task.Delay((int)Math.Pow(2, attempt) * 1000, cancellationToken);
                    continue;
                }

                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"API error ({statusCode}): {error}");
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(1000 * attempt, cancellationToken);
            }
        }

        throw new InvalidOperationException("Transcription failed after retries");
    }

    private static string? GetApiKey() =>
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? CredentialManager.GetApiKey();

    public void Dispose() => _httpClient.Dispose();

    private class TranscriptionResponse
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }
}

