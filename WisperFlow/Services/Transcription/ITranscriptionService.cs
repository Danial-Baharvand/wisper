namespace WisperFlow.Services.Transcription;

/// <summary>
/// Interface for speech-to-text transcription services.
/// </summary>
public interface ITranscriptionService : IDisposable
{
    string ModelId { get; }
    bool IsReady { get; }
    
    Task InitializeAsync(CancellationToken cancellationToken = default);
    
    Task<string> TranscribeAsync(
        string audioFilePath, 
        string? language = null,
        CancellationToken cancellationToken = default);
}

