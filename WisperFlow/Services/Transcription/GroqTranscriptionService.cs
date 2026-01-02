using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services.Transcription;

/// <summary>
/// Transcription service using Groq's Whisper API.
/// Groq provides ultra-fast Whisper inference via their LPU (Language Processing Unit).
/// Uses an OpenAI-compatible API.
/// </summary>
public class GroqTranscriptionService : ITranscriptionService
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly ModelInfo _model;
    private readonly string _apiModelName;
    private const string Endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string ModelId => _model.Id;
    public bool IsReady => !string.IsNullOrEmpty(GetApiKey());

    public GroqTranscriptionService(ILogger logger, ModelInfo model)
    {
        _logger = logger;
        _model = model;
        _apiModelName = GetApiModelName(model.Id);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    private static string GetApiModelName(string modelId) => modelId switch
    {
        "groq-whisper-large-v3-turbo" => "whisper-large-v3-turbo",
        "groq-whisper-large-v3" => "whisper-large-v3",
        _ => "whisper-large-v3-turbo"  // Default to turbo for speed
    };

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null, 
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No Groq API key found. Set GROQ_API_KEY environment variable.");

        var fileInfo = new FileInfo(audioFilePath);
        _logger.LogInformation("Transcribing via Groq API ({Model}), size: {Size:F2}MB", _apiModelName, fileInfo.Length / 1_000_000.0);

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
                content.Add(new StringContent(_apiModelName), "model");
                
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
                    _logger.LogInformation("Groq transcription successful: {Len} chars", transcriptText.Length);
                    _logger.LogInformation("Transcript: {Transcript}", transcriptText);
                    return transcriptText;
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode == 429 || statusCode >= 500)
                {
                    _logger.LogWarning("Groq API returned {StatusCode}, retrying...", statusCode);
                    await Task.Delay((int)Math.Pow(2, attempt) * 1000, cancellationToken);
                    continue;
                }

                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Groq API error ({statusCode}): {error}");
            }
            catch (HttpRequestException ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "Groq API request failed, retrying...");
                await Task.Delay(1000 * attempt, cancellationToken);
            }
        }

        throw new InvalidOperationException("Groq transcription failed after retries");
    }

    private static string? GetApiKey() =>
        Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? CredentialManager.GetGroqApiKey();

    public void Dispose() => _httpClient.Dispose();

    private class TranscriptionResponse
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }
}
