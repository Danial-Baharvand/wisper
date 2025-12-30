using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Lame;

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
        var totalStopwatch = Stopwatch.StartNew();
        
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("No API key found");
            throw new InvalidOperationException("No API key found. Configure in Settings or set OPENAI_API_KEY env var.");
        }

        var fileInfo = new FileInfo(audioFilePath);
        var originalSizeMB = fileInfo.Length / (1024.0 * 1024.0);
        _logger.LogInformation("Original WAV size: {SizeMB:F2}MB", originalSizeMB);

        // Compress to MP3 for faster upload
        string uploadFilePath = audioFilePath;
        string? tempMp3Path = null;
        string contentType = "audio/wav";
        
        var compressionStopwatch = Stopwatch.StartNew();
        try
        {
            tempMp3Path = Path.Combine(Path.GetTempPath(), $"wisperflow_{Guid.NewGuid():N}.mp3");
            CompressToMp3(audioFilePath, tempMp3Path);
            
            var mp3Info = new FileInfo(tempMp3Path);
            var mp3SizeMB = mp3Info.Length / (1024.0 * 1024.0);
            var compressionRatio = originalSizeMB / mp3SizeMB;
            
            compressionStopwatch.Stop();
            _logger.LogInformation("Compressed to MP3: {SizeMB:F2}MB ({Ratio:F1}x smaller) in {Time}ms", 
                mp3SizeMB, compressionRatio, compressionStopwatch.ElapsedMilliseconds);
            
            uploadFilePath = tempMp3Path;
            contentType = "audio/mpeg";
        }
        catch (Exception ex)
        {
            compressionStopwatch.Stop();
            _logger.LogWarning(ex, "MP3 compression failed, using original WAV");
        }

        if (new FileInfo(uploadFilePath).Length / (1024.0 * 1024.0) > MaxFileSizeMB)
            throw new InvalidOperationException($"Recording too large. Max is {MaxFileSizeMB}MB.");

        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var content = new MultipartFormDataContent();

                var fileBytes = await File.ReadAllBytesAsync(uploadFilePath, cancellationToken);
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                content.Add(fileContent, "file", Path.GetFileName(uploadFilePath));
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

                var uploadStopwatch = Stopwatch.StartNew();
                var response = await _httpClient.SendAsync(request, cancellationToken);
                uploadStopwatch.Stop();
                
                _logger.LogInformation("API request completed in {Time}ms", uploadStopwatch.ElapsedMilliseconds);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    var result = JsonSerializer.Deserialize<TranscriptionResponse>(responseJson, JsonOptions);
                    var text = result?.Text ?? string.Empty;
                    
                    totalStopwatch.Stop();
                    _logger.LogInformation("Transcription complete: {Length} chars, total time: {Time}ms", 
                        text.Length, totalStopwatch.ElapsedMilliseconds);
                    _logger.LogInformation("Transcript: {Transcript}", text);
                    
                    // Cleanup temp MP3
                    CleanupTempFile(tempMp3Path);
                    
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
                
                CleanupTempFile(tempMp3Path);
                throw new InvalidOperationException(msg);
            }
            catch (OperationCanceledException) 
            { 
                CleanupTempFile(tempMp3Path);
                throw; 
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                if (attempt == MaxRetries) break;
                await Task.Delay(1000 * attempt, cancellationToken);
            }
        }

        CleanupTempFile(tempMp3Path);
        throw new InvalidOperationException($"Transcription failed after {MaxRetries} attempts", lastException);
    }

    private void CompressToMp3(string wavPath, string mp3Path)
    {
        using var reader = new WaveFileReader(wavPath);
        using var writer = new LameMP3FileWriter(mp3Path, reader.WaveFormat, LAMEPreset.MEDIUM);
        reader.CopyTo(writer);
    }

    private void CleanupTempFile(string? path)
    {
        if (path == null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* ignore */ }
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
