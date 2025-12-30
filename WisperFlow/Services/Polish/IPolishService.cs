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

    /// <summary>
    /// Transform text according to a spoken command.
    /// </summary>
    /// <param name="originalText">The original text to transform.</param>
    /// <param name="command">The spoken command/instruction (e.g., "make this more formal").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transformed text.</returns>
    Task<string> TransformAsync(
        string originalText,
        string command,
        CancellationToken cancellationToken = default);
}

