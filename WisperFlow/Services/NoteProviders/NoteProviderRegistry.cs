namespace WisperFlow.Services.NoteProviders;

/// <summary>
/// Registry for managing note provider instances.
/// Add new providers here to make them available in the UI.
/// </summary>
public static class NoteProviderRegistry
{
    private static readonly Dictionary<string, INoteProvider> _providers = new();
    
    /// <summary>
    /// Register a note provider.
    /// </summary>
    public static void Register(INoteProvider provider)
    {
        _providers[provider.Id] = provider;
    }
    
    /// <summary>
    /// Get a provider by ID.
    /// </summary>
    public static INoteProvider? Get(string id)
    {
        return _providers.TryGetValue(id, out var provider) ? provider : null;
    }
    
    /// <summary>
    /// Get all registered providers.
    /// </summary>
    public static IEnumerable<INoteProvider> GetAll() => _providers.Values;
    
    /// <summary>
    /// Get all provider IDs.
    /// </summary>
    public static IEnumerable<string> GetIds() => _providers.Keys;
}
