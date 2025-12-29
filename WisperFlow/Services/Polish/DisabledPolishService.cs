namespace WisperFlow.Services.Polish;

/// <summary>
/// No-op polish service that returns text unchanged.
/// </summary>
public class DisabledPolishService : IPolishService
{
    public string ModelId => "polish-disabled";
    public bool IsReady => true;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> PolishAsync(string rawText, bool notesMode = false,
        CancellationToken cancellationToken = default) => Task.FromResult(rawText);

    public void Dispose() { }
}

