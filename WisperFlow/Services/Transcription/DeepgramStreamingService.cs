using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using WisperFlow.Services.CodeContext;
using AppModelInfo = WisperFlow.Models.ModelInfo;

namespace WisperFlow.Services.Transcription;

/// <summary>
/// Real-time streaming transcription using Deepgram's WebSocket API.
/// Transcribes audio as it's being recorded for faster results.
/// Uses raw WebSocket for maximum compatibility.
/// </summary>
public class DeepgramStreamingService : ITranscriptionService
{
    private readonly ILogger _logger;
    private readonly AppModelInfo _model;
    private readonly AppSettings _settings;
    private readonly string _deepgramModel;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private readonly ConcurrentQueue<string> _transcriptParts = new();
    private readonly ConcurrentQueue<byte[]> _audioBuffer = new();  // Buffer for audio before WebSocket connects
    private Task? _receiveTask;
    private Task? _connectTask;  // Track connection task
    private bool _isStreaming;
    private bool _isConnected;  // Track if WebSocket is actually connected
    
    public string ModelId => _model.Id;
    public bool IsReady => !string.IsNullOrEmpty(GetApiKey());
    public bool IsConnected => _isConnected;
    public bool IsConnecting => _connectTask != null && !_connectTask.IsCompleted;

    public DeepgramStreamingService(ILogger logger, AppModelInfo model, AppSettings settings)
    {
        _logger = logger;
        _model = model;
        _settings = settings;
        
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
            throw new InvalidOperationException("Deepgram API key not configured.");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Start streaming transcription - returns immediately, connection happens in background.
    /// Audio chunks sent before connection completes are buffered and sent once connected.
    /// </summary>
    /// <param name="language">Language code for transcription</param>
    /// <param name="dynamicKeywords">Optional keywords from code context (file names, symbols)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public void StartStreamingAsync(string? language = null, IEnumerable<string>? dynamicKeywords = null, CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Deepgram API key not configured.");

        // Clear state
        _transcriptParts.Clear();
        while (_audioBuffer.TryDequeue(out _)) { }  // Clear buffer
        _isConnected = false;
        _isStreaming = true;  // Mark as streaming immediately so audio starts buffering
        _receiveCts = new CancellationTokenSource();

        // Build WebSocket URL with parameters
        var queryParams = new List<string>
        {
            $"model={_deepgramModel}",
            $"language={language ?? "en"}",
            "encoding=linear16",
            "sample_rate=16000",
            "channels=1",
            "interim_results=false"  // Only final results for reliability
        };
        
        // Core formatting options
        if (_settings.DeepgramSmartFormat) queryParams.Add("smart_format=true");
        if (_settings.DeepgramPunctuate) queryParams.Add("punctuate=true");
        if (_settings.DeepgramDiarize) queryParams.Add("diarize=true");
        if (_settings.DeepgramUtterances) queryParams.Add("utterances=true");
        if (_settings.DeepgramFillerWords) queryParams.Add("filler_words=true");
        
        // Dictation-optimized options
        if (_settings.DeepgramDictation) queryParams.Add("dictation=true");
        if (_settings.DeepgramNumerals) queryParams.Add("numerals=true");
        
        // Endpointing for faster streaming (critical for speed!)
        if (_settings.DeepgramEndpointing > 0)
        {
            queryParams.Add($"endpointing={_settings.DeepgramEndpointing}");
        }
        
        // Privacy options
        if (_settings.DeepgramMipOptOut) queryParams.Add("mip_opt_out=true");
        if (!string.IsNullOrWhiteSpace(_settings.DeepgramRedact))
        {
            foreach (var redactType in _settings.DeepgramRedact.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                queryParams.Add($"redact={redactType}");
            }
        }
        
        // Keyword boosting - combine settings keywords with dynamic keywords from code context
        var allKeywords = new List<string>();
        
        // Add settings-based keywords first (these bypass the English filter)
        if (!string.IsNullOrWhiteSpace(_settings.DeepgramKeywords))
        {
            allKeywords.AddRange(_settings.DeepgramKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        
        // Add dynamic keywords from code context, but only non-English words
        if (dynamicKeywords != null)
        {
            foreach (var keyword in dynamicKeywords)
            {
                if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 2)
                    continue;
                
                // Check if this keyword contains any non-English parts
                if (ContainsNonEnglishPart(keyword))
                {
                    allKeywords.Add(keyword);
                }
            }
        }
        
        // Deduplicate and limit by total token count (Deepgram limits to 500 tokens total)
        // Be conservative - use max 300 tokens to account for tokenization differences
        var uniqueKeywords = new List<string>();
        int totalTokens = 0;
        const int maxTokens = 300; // Conservative limit
        
        foreach (var keyword in allKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k) && k.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int estimatedTokens = EstimateTokenCount(keyword);
            
            if (totalTokens + estimatedTokens <= maxTokens)
            {
                uniqueKeywords.Add(keyword);
                totalTokens += estimatedTokens;
            }
            else
            {
                break;
            }
        }
        
        // Use keyterm for Nova-3, keywords for older models
        var paramName = _deepgramModel.StartsWith("nova-3") ? "keyterm" : "keywords";
        foreach (var keyword in uniqueKeywords)
        {
            queryParams.Add($"{paramName}={Uri.EscapeDataString(keyword)}");
        }
        
        if (uniqueKeywords.Count > 0)
        {
            _logger.LogInformation("Deepgram streaming with {Count} {ParamName}s (~{Tokens} tokens)", 
                uniqueKeywords.Count, paramName, totalTokens);
        }
        
        var url = $"wss://api.deepgram.com/v1/listen?{string.Join("&", queryParams)}";
        
        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("Authorization", $"Token {apiKey}");
        
        // Start connection in background - don't block!
        _connectTask = ConnectAndFlushBufferAsync(url, cancellationToken);
        
        _logger.LogInformation("Deepgram streaming initiated (connecting in background): {Model}", _deepgramModel);
    }
    
    /// <summary>
    /// Checks if a keyword contains any non-English word parts.
    /// Keywords with only English words don't need boosting since Deepgram already knows them.
    /// </summary>
    private static bool ContainsNonEnglishPart(string keyword)
    {
        var parts = SplitIntoParts(keyword);
        
        foreach (var part in parts)
        {
            if (part.Length < 2) continue;
            
            if (!EnglishDictionary.IsEnglishWord(part))
            {
                return true; // Found a non-English part
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Splits a keyword into word parts (by underscore, hyphen, dot, CamelCase).
    /// </summary>
    private static List<string> SplitIntoParts(string keyword)
    {
        var parts = new List<string>();
        
        var segments = keyword.Split(new[] { '_', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var segment in segments)
        {
            var camelParts = Regex.Split(segment, @"(?<!^)(?=[A-Z])");
            foreach (var part in camelParts)
            {
                if (!string.IsNullOrEmpty(part))
                {
                    parts.Add(part.ToLowerInvariant());
                }
            }
        }
        
        return parts;
    }
    
    /// <summary>
    /// Estimates the number of tokens in a keyword for Deepgram's limit.
    /// </summary>
    private static int EstimateTokenCount(string keyword)
    {
        if (string.IsNullOrEmpty(keyword))
            return 0;
        
        return Math.Max(1, SplitIntoParts(keyword).Count);
    }
    
    /// <summary>
    /// Connect to WebSocket and flush any buffered audio once connected.
    /// </summary>
    private async Task ConnectAndFlushBufferAsync(string url, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _webSocket!.ConnectAsync(new Uri(url), cancellationToken);
            _isConnected = true;
            
            _logger.LogInformation("Deepgram streaming connected in {Ms}ms", stopwatch.ElapsedMilliseconds);
            
            // Start receiving responses in background
            _receiveTask = ReceiveResponsesAsync(_receiveCts!.Token);
            
            // Flush any buffered audio that arrived before connection
            var bufferedChunks = 0;
            while (_audioBuffer.TryDequeue(out var chunk))
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(chunk),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancellationToken);
                    bufferedChunks++;
                }
            }
            
            if (bufferedChunks > 0)
            {
                _logger.LogDebug("Flushed {Count} buffered audio chunks", bufferedChunks);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Deepgram streaming API after {Ms}ms", stopwatch.ElapsedMilliseconds);
            _webSocket?.Dispose();
            _webSocket = null;
            _isConnected = false;
            _isStreaming = false;
        }
    }

    /// <summary>
    /// Send an audio chunk to Deepgram during recording.
    /// If WebSocket isn't connected yet, buffers the audio for later.
    /// </summary>
    public async Task SendAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (!_isStreaming) return;
        
        // If not connected yet, buffer the audio
        if (!_isConnected || _webSocket?.State != WebSocketState.Open)
        {
            _audioBuffer.Enqueue(audioData);
            return;
        }

        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(audioData),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send audio chunk");
        }
    }

    /// <summary>
    /// Stop streaming and get the final transcript.
    /// </summary>
    public async Task<string> StopStreamingAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _isStreaming = false;
        
        // Wait for connection to complete if still connecting (with timeout)
        if (_connectTask != null && !_connectTask.IsCompleted)
        {
            try
            {
                await _connectTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                _logger.LogDebug("Connection completed at {Ms}ms", stopwatch.ElapsedMilliseconds);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Connection timed out, proceeding with available data");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connection failed, proceeding with available data");
            }
        }
        
        // Flush any remaining buffered audio
        if (_isConnected && _webSocket?.State == WebSocketState.Open)
        {
            while (_audioBuffer.TryDequeue(out var chunk))
            {
                try
                {
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(chunk),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancellationToken);
                }
                catch { break; }
            }
        }
        
        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                // Step 1: Send Finalize to flush any remaining audio (this triggers final results)
                var finalizeMessage = Encoding.UTF8.GetBytes("{\"type\": \"Finalize\"}");
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(finalizeMessage),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
                
                _logger.LogDebug("Sent Finalize message at {Ms}ms", stopwatch.ElapsedMilliseconds);
                
                // Step 2: Wait briefly for final results (endpointing should have processed most already)
                // With endpointing=300ms, most results are already in. Just wait for stragglers.
                await Task.Delay(150, cancellationToken);
                
                _logger.LogDebug("Waited for final results at {Ms}ms", stopwatch.ElapsedMilliseconds);
                
                // Step 3: Close the stream
                var closeMessage = Encoding.UTF8.GetBytes("{\"type\": \"CloseStream\"}");
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(closeMessage),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
                
                // Step 4: Close WebSocket gracefully (with short timeout)
                using var closeCts = new CancellationTokenSource(100);  // Reduced from 200ms
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("WebSocket close timed out, continuing anyway");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing Deepgram WebSocket");
            }
        }
        
        _logger.LogDebug("WebSocket closed at {Ms}ms", stopwatch.ElapsedMilliseconds);
        
        // Cancel the receive task
        _receiveCts?.Cancel();
        
        // Don't wait long for receive task - we already have the results
        try
        {
            if (_receiveTask != null)
                await _receiveTask.WaitAsync(TimeSpan.FromMilliseconds(50));  // Reduced from 100ms
        }
        catch { }
        
        _webSocket?.Dispose();
        _webSocket = null;
        
        // Combine all transcript parts
        var allParts = new StringBuilder();
        while (_transcriptParts.TryDequeue(out var part))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                if (allParts.Length > 0) allParts.Append(' ');
                allParts.Append(part);
            }
        }
        
        var finalTranscript = allParts.ToString().Trim();
        stopwatch.Stop();
        _logger.LogInformation("Streaming complete in {Ms}ms, transcript: {Len} chars", 
            stopwatch.ElapsedMilliseconds, finalTranscript.Length);
        _logger.LogInformation("Transcript: {Transcript}", finalTranscript);
        
        return finalTranscript;
    }

    private async Task ReceiveResponsesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        var messageBuffer = new StringBuilder();
        
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    
                    if (result.EndOfMessage)
                    {
                        var json = messageBuffer.ToString();
                        messageBuffer.Clear();
                        ProcessResponse(json);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogDebug("WebSocket close received");
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug("WebSocket closed");
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving WebSocket messages");
        }
    }

    private void ProcessResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // Check message type
            if (root.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();
                
                if (type == "Results")
                {
                    // Check if this is a final result
                    var isFinal = root.TryGetProperty("is_final", out var isFinalEl) && isFinalEl.GetBoolean();
                    
                    if (isFinal && root.TryGetProperty("channel", out var channel))
                    {
                        if (channel.TryGetProperty("alternatives", out var alternatives))
                        {
                            foreach (var alt in alternatives.EnumerateArray())
                            {
                                if (alt.TryGetProperty("transcript", out var transcript))
                                {
                                    var text = transcript.GetString();
                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        _transcriptParts.Enqueue(text);
                                        _logger.LogDebug("Received final transcript segment: '{Text}'", 
                                            text.Length > 50 ? text[..50] + "..." : text);
                                    }
                                }
                                break; // Just use first alternative
                            }
                        }
                    }
                }
                else if (type == "Metadata")
                {
                    _logger.LogDebug("Received metadata from Deepgram");
                }
                else if (type == "Error")
                {
                    var message = root.TryGetProperty("message", out var msgEl) 
                        ? msgEl.GetString() 
                        : "Unknown error";
                    _logger.LogError("Deepgram streaming error: {Message}", message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Deepgram response");
        }
    }

    /// <summary>
    /// For ITranscriptionService compatibility - falls back to REST API.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioFilePath, 
        string? language = null, 
        CancellationToken cancellationToken = default)
    {
        // For file-based transcription, use the REST API
        var restService = new DeepgramTranscriptionService(_logger, _model, _settings);
        return await restService.TranscribeAsync(audioFilePath, language, cancellationToken);
    }

    private static string? GetApiKey()
    {
        var key = CredentialManager.GetDeepgramApiKey();
        if (!string.IsNullOrEmpty(key)) return key;
        return Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY");
    }

    public void Dispose()
    {
        _receiveCts?.Cancel();
        _webSocket?.Dispose();
        _webSocket = null;
        _isStreaming = false;
        _isConnected = false;
        while (_audioBuffer.TryDequeue(out _)) { }  // Clear buffer
        GC.SuppressFinalize(this);
    }
}
