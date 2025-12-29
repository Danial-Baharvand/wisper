namespace WisperFlow.Services.Polish;

/// <summary>
/// Interface for text polishing/cleanup services.
/// </summary>
public interface IPolishService : IDisposable
{
    string ModelId { get; }
    bool IsReady { get; }
    
    Task InitializeAsync(CancellationToken cancellationToken = default);
    
    Task<string> PolishAsync(
        string rawText, 
        bool notesMode = false,
        CancellationToken cancellationToken = default);
}

