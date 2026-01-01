using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WisperFlow.Services.CodeContext;

/// <summary>
/// Status of cache validation.
/// </summary>
public enum CacheStatus
{
    /// <summary>All paths are valid and working.</summary>
    Valid,
    
    /// <summary>Some paths work, others need re-discovery. Extraction continues with working paths.</summary>
    PartiallyValid,
    
    /// <summary>Cache is invalid (3 strikes) and needs full re-discovery.</summary>
    Invalid,
    
    /// <summary>Cache doesn't exist yet.</summary>
    NotFound
}

/// <summary>
/// Cache for accessibility tree paths.
/// Stores the path indices to reach specific UI elements (tabs, explorer, code editor)
/// for faster subsequent access without full tree traversal.
/// 
/// Uses a 3-strikes rule: paths are marked PartiallyValid on failure, and only after
/// 3 consecutive failures is the cache marked Invalid and deleted.
/// </summary>
internal class PathCache
{
    private const string CacheFolder = "WisperFlow";
    private const string CacheSubFolder = "AccessibilityCache";
    
    // Core paths
    public string? ProcessName { get; set; }
    public int[]? TabContainerPath { get; set; }
    public int[]? ExplorerContainerPath { get; set; }
    public int[]? CodeEditorPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Depth hints for validation
    public int TabContainerDepth { get; set; }
    public int ExplorerContainerDepth { get; set; }
    public int CodeEditorDepth { get; set; }
    
    // Validation state (not persisted - runtime only)
    [JsonIgnore]
    public int ValidationFailures { get; set; }
    
    [JsonIgnore]
    public DateTime? LastValidated { get; set; }
    
    /// <summary>
    /// Returns true if we have at least one useful cached path (tabs OR code).
    /// </summary>
    public bool IsComplete => TabContainerPath != null || CodeEditorPath != null;
    
    /// <summary>
    /// Returns true if all main paths are cached (tabs AND code).
    /// </summary>
    public bool IsFullyComplete => TabContainerPath != null && CodeEditorPath != null;
    
    /// <summary>
    /// Saves this cache to disk.
    /// </summary>
    public void Save(string processName)
    {
        try
        {
            var folder = GetCacheFolder();
            Directory.CreateDirectory(folder);
            
            var path = Path.Combine(folder, $"{processName.ToLower()}_paths.json");
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            
            CacheLogger.LogCache("SAVED", $"Process={processName}, Tabs={TabContainerPath != null}, Explorer={ExplorerContainerPath != null}, Code={CodeEditorPath != null}");
        }
        catch (Exception ex)
        {
            CacheLogger.LogCache("SAVE_FAILED", ex.Message);
        }
    }
    
    /// <summary>
    /// Loads a cached path set for the given process name.
    /// Returns null if cache doesn't exist or is expired.
    /// </summary>
    public static PathCache? Load(string processName)
    {
        try
        {
            var path = Path.Combine(GetCacheFolder(), $"{processName.ToLower()}_paths.json");
            if (!File.Exists(path))
            {
                CacheLogger.LogCache("LOAD", $"No cache file for {processName}");
                return null;
            }
            
            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<PathCache>(json);
            
            // Expire after 24 hours
            if (cache != null && (DateTime.UtcNow - cache.CreatedAt).TotalHours > 24)
            {
                CacheLogger.LogCache("EXPIRED", $"Cache for {processName} is older than 24 hours");
                Delete(processName);
                return null;
            }
            
            CacheLogger.LogCache("LOADED", $"Process={processName}, Tabs={cache?.TabContainerPath != null}, Explorer={cache?.ExplorerContainerPath != null}, Code={cache?.CodeEditorPath != null}");
            return cache;
        }
        catch (Exception ex)
        {
            CacheLogger.LogCache("LOAD_FAILED", ex.Message);
            return null;
        }
    }
    
    /// <summary>
    /// Deletes the cached paths for a process.
    /// </summary>
    public static void Delete(string processName)
    {
        try
        {
            if (string.IsNullOrEmpty(processName))
                return;
            var path = Path.Combine(GetCacheFolder(), $"{processName.ToLower()}_paths.json");
            if (File.Exists(path))
            {
                File.Delete(path);
                CacheLogger.LogCache("DELETED", $"Cache for {processName}");
            }
        }
        catch (Exception ex)
        {
            CacheLogger.LogCache("DELETE_FAILED", ex.Message);
        }
    }
    
    private static string GetCacheFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CacheFolder,
            CacheSubFolder);
    }
    
    public override string ToString()
    {
        return $"PathCache[Tabs={TabContainerPath?.Length ?? 0} hops, Explorer={ExplorerContainerPath?.Length ?? 0} hops, Code={CodeEditorPath?.Length ?? 0} hops, Failures={ValidationFailures}]";
    }
}
