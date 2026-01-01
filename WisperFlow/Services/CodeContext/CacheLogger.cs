using System.IO;
using System.Text;

namespace WisperFlow.Services.CodeContext;

/// <summary>
/// Dedicated logger for cache operations.
/// Writes to %LOCALAPPDATA%\WisperFlow\cache_debug.log for monitoring cache behavior.
/// </summary>
internal static class CacheLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WisperFlow", "cache_debug.log");
    
    private static readonly object _lock = new();
    private const int MaxLogSizeBytes = 1_000_000; // 1MB max log size
    
    /// <summary>
    /// Enable/disable cache logging. When false, all logging is a no-op for performance.
    /// </summary>
    public static bool Enabled { get; set; } = true; // Enable for debugging
    
    /// <summary>
    /// Logs a general message.
    /// </summary>
    public static void Log(string message)
    {
        if (!Enabled) return;
        WriteLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }
    
    /// <summary>
    /// Logs cache validation result.
    /// </summary>
    public static void LogValidation(CacheStatus status, string details)
    {
        if (!Enabled) return;
        string strikeInfo = status == CacheStatus.PartiallyValid ? " - Strike" : "";
        WriteLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] VALIDATION: {status} ({details}){strikeInfo}");
    }
    
    /// <summary>
    /// Logs extraction result.
    /// </summary>
    public static void LogExtraction(string method, int files, int symbols, long ms)
    {
        if (!Enabled) return;
        WriteLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] EXTRACTION: {method} | {files} files, {symbols} symbols | {ms}ms");
    }
    
    /// <summary>
    /// Logs path discovery during full traversal.
    /// </summary>
    public static void LogPathDiscovery(string pathType, int[]? path, int depth)
    {
        if (!Enabled) return;
        string pathStr = path != null ? string.Join(",", path) : "null";
        WriteLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DISCOVERY: {pathType} path=[{pathStr}] depth={depth}");
    }
    
    /// <summary>
    /// Logs background refresh events.
    /// </summary>
    public static void LogBackground(string message)
    {
        if (!Enabled) return;
        WriteLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] BACKGROUND: {message}");
    }
    
    /// <summary>
    /// Logs cache save/load events.
    /// </summary>
    public static void LogCache(string action, string details)
    {
        if (!Enabled) return;
        WriteLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CACHE: {action} - {details}");
    }
    
    private static void WriteLog(string line)
    {
        try
        {
            lock (_lock)
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                // Rotate log if too large
                if (File.Exists(LogPath))
                {
                    var info = new FileInfo(LogPath);
                    if (info.Length > MaxLogSizeBytes)
                    {
                        // Keep last half of log
                        var lines = File.ReadAllLines(LogPath);
                        var keepLines = lines.Skip(lines.Length / 2).ToArray();
                        File.WriteAllLines(LogPath, keepLines);
                    }
                }
                
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Silently fail - logging should never break the app
        }
    }
}
