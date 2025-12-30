using System.IO;
using Deepgram;
using Deepgram.Clients.Listen.v1.REST;
using Deepgram.Models.Listen.v1.REST;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using AppModelInfo = WisperFlow.Models.ModelInfo;

namespace WisperFlow.Services.Transcription;

/// <summary>
/// Transcription service using Deepgram's speech-to-text API.
/// Supports multiple models: Nova-3 (fastest), Nova-2, Whisper Cloud, Base.
/// Configurable via AppSettings for streaming, formatting, diarization, etc.
/// </summary>
public class DeepgramTranscriptionService : ITranscriptionService
{
    private readonly ILogger _logger;
    private readonly AppModelInfo _model;
    private readonly AppSettings _settings;
    private readonly string _deepgramModel;
    private ListenRESTClient? _client;
    
    public string ModelId => _model.Id;
    public bool IsReady => !string.IsNullOrEmpty(GetApiKey());

    public DeepgramTranscriptionService(ILogger logger, AppModelInfo model, AppSettings settings)
    {
        _logger = logger;
        _model = model;
        _settings = settings;
        
        // Map our model IDs to Deepgram model names
        _deepgramModel = model.Id switch
        {
            "deepgram-nova-3" => "nova-3",
            "deepgram-nova-2" => "nova-2",
            "deepgram-nova-2-medical" => "nova-2-medical",
            "deepgram-whisper-cloud" => "whisper-large",
            "deepgram-base" => "base",
            _ => "nova-2"
        };
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Deepgram API key not configured. Please add it in Settings.");
        }
        
        // Initialize the Deepgram client using the new ListenRESTClient
        _client = new ListenRESTClient(apiKey);
        
        _logger.LogInformation("Deepgram client initialized for model: {Model}", _deepgramModel);
        return Task.CompletedTask;
    }

    public async Task<string> TranscribeAsync(
        string audioFilePath, 
        string? language = null, 
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Deepgram API key not configured. Please add it in Settings.");

        // Ensure client is initialized
        if (_client == null)
        {
            await InitializeAsync(cancellationToken);
        }

        var fileInfo = new FileInfo(audioFilePath);
        _logger.LogInformation("Transcribing via Deepgram {Model}, size: {Size:F2}MB, streaming: {Streaming}", 
            _deepgramModel, fileInfo.Length / 1_000_000.0, _settings.DeepgramStreaming);

        var startTime = DateTime.UtcNow;

        try
        {
            // Read the audio file
            var audioBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
            
            // Build transcription options from settings
            var options = new PreRecordedSchema
            {
                Model = _deepgramModel,
                Language = string.IsNullOrWhiteSpace(language) || language == "auto" ? "en" : language,
                
                // Formatting options from settings
                SmartFormat = _settings.DeepgramSmartFormat,
                Punctuate = _settings.DeepgramPunctuate,
                Utterances = _settings.DeepgramUtterances,
                Paragraphs = _settings.DeepgramParagraphs,
                
                // Advanced options
                Diarize = _settings.DeepgramDiarize,
                FillerWords = _settings.DeepgramFillerWords,
                
                // Dictation-optimized options
                Dictation = _settings.DeepgramDictation,
                Numerals = _settings.DeepgramNumerals
            };
            
            // Profanity filter
            if (_settings.DeepgramProfanityFilter != "false")
            {
                options.ProfanityFilter = _settings.DeepgramProfanityFilter == "true";
            }
            
            // Redaction
            if (!string.IsNullOrWhiteSpace(_settings.DeepgramRedact))
            {
                options.Redact = _settings.DeepgramRedact.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            
            // Keyword boosting
            if (!string.IsNullOrWhiteSpace(_settings.DeepgramKeywords))
            {
                var keywords = _settings.DeepgramKeywords
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (keywords.Count > 0)
                {
                    options.Keywords = keywords;
                }
            }

            // Create cancellation token source from the token
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            
            // Transcribe the audio file
            var response = await _client!.TranscribeFile(audioBytes, options, cts);

            var elapsed = DateTime.UtcNow - startTime;
            
            // Extract the transcript from the response
            string transcript = "";
            
            if (response?.Results?.Channels != null && response.Results.Channels.Count > 0)
            {
                var channel = response.Results.Channels[0];
                var alternative = channel.Alternatives?.FirstOrDefault();
                
                if (alternative != null)
                {
                    if (_settings.DeepgramParagraphs && alternative.Paragraphs?.Transcript != null)
                    {
                        // Use paragraph-formatted transcript if available
                        transcript = alternative.Paragraphs.Transcript;
                    }
                    else if (_settings.DeepgramDiarize && alternative.Words != null && alternative.Words.Count > 0)
                    {
                        // Build speaker-labeled transcript for diarization
                        transcript = BuildDiarizedTranscript(alternative.Words);
                    }
                    else
                    {
                        // Standard transcript
                        transcript = alternative.Transcript ?? "";
                    }
                }
            }

            _logger.LogInformation("Deepgram transcription complete in {Time:F2}s: {Len} chars", 
                elapsed.TotalSeconds, transcript.Length);
            _logger.LogInformation("Transcript: {Transcript}", transcript);

            return transcript;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Deepgram transcription failed: {Message}", ex.Message);
            throw new InvalidOperationException($"Deepgram transcription failed: {ex.Message}", ex);
        }
    }

    private string BuildDiarizedTranscript(IReadOnlyList<Word> words)
    {
        if (words == null || words.Count == 0)
            return "";

        var result = new System.Text.StringBuilder();
        int? currentSpeaker = null;

        foreach (var word in words)
        {
            if (word.Speaker != currentSpeaker)
            {
                if (result.Length > 0)
                    result.AppendLine();
                currentSpeaker = word.Speaker;
                result.Append($"[Speaker {currentSpeaker}]: ");
            }
            // Use PunctuatedWord property (Deepgram SDK v5)
            result.Append(word.PunctuatedWord ?? "");
            result.Append(' ');
        }

        return result.ToString().Trim();
    }

    private static string? GetApiKey()
    {
        // Try Credential Manager first (Deepgram-specific)
        var key = CredentialManager.GetDeepgramApiKey();
        if (!string.IsNullOrEmpty(key)) return key;
        
        // Fall back to environment variable
        return Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY");
    }

    public void Dispose()
    {
        // Deepgram client doesn't need explicit disposal
        _client = null;
        GC.SuppressFinalize(this);
    }
}
