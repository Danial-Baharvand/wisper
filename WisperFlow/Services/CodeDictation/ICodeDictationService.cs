namespace WisperFlow.Services.CodeDictation;

/// <summary>
/// Service for converting natural language speech to programming code.
/// </summary>
public interface ICodeDictationService : IDisposable
{
    /// <summary>
    /// The model ID this service uses.
    /// </summary>
    string ModelId { get; }
    
    /// <summary>
    /// Whether the service is ready for inference.
    /// </summary>
    bool IsReady { get; }
    
    /// <summary>
    /// Initialize the service (load model if needed).
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Convert natural language dictation to code.
    /// </summary>
    /// <param name="naturalLanguage">The spoken dictation text (e.g., "for i in range n colon").</param>
    /// <param name="language">The target programming language (e.g., "python").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated code with proper syntax and indentation.</returns>
    Task<string> ConvertToCodeAsync(
        string naturalLanguage, 
        string language, 
        CancellationToken cancellationToken = default);
}

