using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accessibility;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services.CodeContext;

/// <summary>
/// Provides code context keywords for Deepgram speech recognition and LLM prompts.
/// Extracts file names and code symbols from VS Code/Cursor using accessibility APIs.
/// 
/// Keywords distribution for Deepgram (100 total):
/// - 30 file names (prioritize open tabs, then explorer)
/// - 70 symbols (classes, functions, variables)
/// 
/// For LLM prompts:
/// - 100 file names, 400 symbols max
/// 
/// IMPORTANT: Only activates when a code editor is in the FOREGROUND.
/// Uses per-window path caching and per-project content caching.
/// </summary>
public class CodeContextService : IDisposable
{
    private const int MAX_DEEPGRAM_KEYWORDS = 100;
    private const int TARGET_FILE_KEYWORDS = 30;
    private const int TARGET_SYMBOL_KEYWORDS = 70;
    private const int MAX_FILE_QUEUE_SIZE = 400;
    private const int MAX_PROMPT_FILES = 100;
    private const int MAX_PROMPT_SYMBOLS = 400;
    private const int MAX_CHILDREN_BUFFER = 200;
    private const int EXTRACTION_CACHE_SECONDS = 60;
    
    private readonly ILogger _logger;
    private readonly object _lock = new();
    
    // === Foreground Window Detection ===
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    
    // === Per-Window Caches (Runtime Only) ===
    // Maps window handle to its path cache (node paths can differ per window due to UI layout)
    private readonly Dictionary<IntPtr, PathCache> _windowPathCaches = new();
    
    // Maps window handle to known editor process (avoids Process.GetProcesses())
    private readonly Dictionary<IntPtr, (Process process, string editorName)> _knownEditorWindows = new();
    
    // === Per-Project Caches (Persistent) ===
    // Maps project name to its content cache (files/symbols)
    private readonly Dictionary<string, ProjectContentCache> _projectContentCaches = new();
    
    // === Current State ===
    private IntPtr _currentWindowHandle;
    private string? _currentProjectName;
    private string? _currentEditorName;
    private PathCache? _currentPathCache;
    private ProjectContentCache? _currentContentCache;
    private DateTime _lastExtraction;
    private bool _isExtracting;
    
    // Reusable buffer for NavigateToPath to reduce GC pressure
    private readonly object[] _childBuffer = new object[MAX_CHILDREN_BUFFER];
    
    // Supported editor process names
    private static readonly string[] SupportedEditors = { "Cursor", "Code", "VSCodium" };
    
    // Cache directory
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WisperFlow", "ProjectCaches");
    
    /// <summary>
    /// Enable diagnostic features like DumpAccessibilityTreeAsync.
    /// Set to true for debugging, false for production.
    /// </summary>
    public static bool EnableDiagnostics { get; set; } = false;
    
    public CodeContextService(ILogger<CodeContextService> logger)
    {
        _logger = logger;
        
        // Ensure cache directory exists
        try
        {
            if (!Directory.Exists(CacheDir))
                Directory.CreateDirectory(CacheDir);
        }
        catch { /* Ignore - caching will just fail gracefully */ }
    }
    
    #region Foreground Detection and Project Identification
    
    /// <summary>
    /// Checks if the foreground window is a supported code editor.
    /// Returns the window handle if it is, IntPtr.Zero otherwise.
    /// This is very cheap (~1μs) - just a Win32 call.
    /// </summary>
    private IntPtr GetForegroundEditorWindow()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return IntPtr.Zero;
        
        // Fast path: check if we already know this window
        if (_knownEditorWindows.ContainsKey(foreground))
            return foreground;
        
        // Slower path: get process ID and check if it's an editor
        GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0)
            return IntPtr.Zero;
        
        try
        {
            var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            
            foreach (var editor in SupportedEditors)
            {
                if (processName.Equals(editor, StringComparison.OrdinalIgnoreCase))
                {
                    // Cache this window for fast future lookups
                    _knownEditorWindows[foreground] = (process, editor);
                    CacheLogger.Log($"Discovered editor window: {editor} (hwnd={foreground}, pid={processId})");
                    return foreground;
                }
            }
        }
        catch
        {
            // Process may have exited
        }
        
        return IntPtr.Zero;
    }
    
    /// <summary>
    /// Extracts project name from window title.
    /// Cursor/VS Code titles are typically: "filename - ProjectName - Cursor" or "ProjectName - Cursor"
    /// </summary>
    private string? GetProjectNameFromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;
        
        var sb = new StringBuilder(512);
        int length = GetWindowText(hwnd, sb, 512);
        if (length == 0)
            return null;
        
        var title = sb.ToString();
        
        // Parse: "file.py - ProjectName - Cursor" or "ProjectName - Cursor"
        // We want the second-to-last segment
        var parts = title.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length >= 2)
        {
            // The last part is the editor name (Cursor, Code, etc.)
            // The second-to-last is usually the project/folder name
            var projectName = parts[^2].Trim();
            
            // Validate: project name shouldn't be too long or contain invalid chars
            if (projectName.Length > 0 && projectName.Length < 100 && 
                !projectName.Contains(Path.DirectorySeparatorChar) &&
                !projectName.Contains(Path.AltDirectorySeparatorChar))
            {
                return projectName;
            }
        }
        
        // Fallback: use the whole title (sanitized)
        var sanitized = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
        return sanitized.Length > 50 ? sanitized.Substring(0, 50) : sanitized;
    }
    
    /// <summary>
    /// Switches to the appropriate caches for the current foreground window.
    /// Returns true if a valid editor is in foreground and caches are ready.
    /// </summary>
    private bool SwitchToCurrentWindowContext()
    {
        var hwnd = GetForegroundEditorWindow();
        if (hwnd == IntPtr.Zero)
        {
            _currentWindowHandle = IntPtr.Zero;
            _currentProjectName = null;
            _currentPathCache = null;
            _currentContentCache = null;
            return false;
        }
        
        // Check if we're already on this window
        if (hwnd == _currentWindowHandle && _currentPathCache != null && _currentContentCache != null)
        {
            return true;
        }
        
        _currentWindowHandle = hwnd;
        
        // Get or create path cache for this window (runtime only)
        if (!_windowPathCaches.TryGetValue(hwnd, out _currentPathCache))
        {
            _currentPathCache = new PathCache();
            _windowPathCaches[hwnd] = _currentPathCache;
            CacheLogger.Log($"Created new path cache for window {hwnd}");
        }
        
        // Get project name and load/create content cache
        var projectName = GetProjectNameFromWindow(hwnd);
        if (projectName != _currentProjectName || _currentContentCache == null)
        {
            _currentProjectName = projectName;
            
            if (!string.IsNullOrEmpty(projectName))
            {
                if (!_projectContentCaches.TryGetValue(projectName, out _currentContentCache))
                {
                    // Try to load from disk first
                    _currentContentCache = LoadProjectContentCache(projectName) ?? new ProjectContentCache();
                    _projectContentCaches[projectName] = _currentContentCache;
                    CacheLogger.Log($"Loaded/created content cache for project '{projectName}': {_currentContentCache.TabFiles.Count} tabs, {_currentContentCache.ExplorerFiles.Count} explorer, {_currentContentCache.Symbols.Count} symbols");
                }
            }
            else
            {
                // No project name - use a default cache
                _currentContentCache = new ProjectContentCache();
            }
        }
        
        // Get editor name for this window
        if (_knownEditorWindows.TryGetValue(hwnd, out var editorInfo))
        {
            _currentEditorName = editorInfo.editorName;
        }
        
        return true;
    }
    
    /// <summary>
    /// Gets the Process for the current foreground editor window.
    /// Returns null if no editor is in foreground.
    /// </summary>
    private Process? GetCurrentEditorProcess()
    {
        if (_currentWindowHandle == IntPtr.Zero)
            return null;
        
        if (_knownEditorWindows.TryGetValue(_currentWindowHandle, out var info))
            return info.process;
        
        return null;
    }
    
    #endregion
    
    #region Project Content Cache Persistence
    
    private string GetProjectCachePath(string projectName)
    {
        // Sanitize project name for filename
        var safeName = string.Join("_", projectName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CacheDir, $"{safeName}_content.json");
    }
    
    private ProjectContentCache? LoadProjectContentCache(string projectName)
    {
        try
        {
            var path = GetProjectCachePath(projectName);
            if (!File.Exists(path))
                return null;
            
            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<ProjectContentCache>(json);
            
            if (cache != null)
            {
                CacheLogger.LogCache("Loaded", $"Project '{projectName}': {cache.TabFiles.Count} tabs, {cache.ExplorerFiles.Count} explorer, {cache.Symbols.Count} symbols");
            }
            
            return cache;
        }
        catch (Exception ex)
        {
            CacheLogger.Log($"Failed to load project cache '{projectName}': {ex.Message}");
            return null;
        }
    }
    
    private void SaveProjectContentCache(string projectName, ProjectContentCache cache)
    {
        try
        {
            var path = GetProjectCachePath(projectName);
            cache.LastUpdated = DateTime.UtcNow;
            
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            
            CacheLogger.LogCache("Saved", $"Project '{projectName}': {cache.TabFiles.Count} tabs, {cache.ExplorerFiles.Count} explorer, {cache.Symbols.Count} symbols");
        }
        catch (Exception ex)
        {
            CacheLogger.Log($"Failed to save project cache '{projectName}': {ex.Message}");
        }
    }
    
    #endregion
    
    /// <summary>
    /// Gets keywords for Deepgram based on current code editor context.
    /// Returns empty list if no supported editor is in the FOREGROUND.
    /// Prioritizes non-English words since Deepgram already knows common English.
    /// 
    /// KEY DESIGN:
    /// - Only extracts when a code editor is in the FOREGROUND (not just running)
    /// - Uses per-window path caches (runtime) and per-project content caches (persistent)
    /// - If we have cached node paths, use them for fast (~50ms) extraction
    /// - If cache is invalid or missing, return cached data immediately and refresh paths in background
    /// - Never blocks polish operations with slow 5-second full traversals
    /// </summary>
    public async Task<List<string>> GetKeywordsForDeepgramAsync()
    {
        // STEP 1: Check if a code editor is in the FOREGROUND - if not, return empty immediately
        if (!SwitchToCurrentWindowContext())
        {
            _logger.LogDebug("No code editor in foreground, skipping extraction");
            return new List<string>();
        }
        
        var process = GetCurrentEditorProcess();
        if (process == null)
        {
            _logger.LogDebug("Could not get editor process, skipping extraction");
            return new List<string>();
        }
        
        CacheLogger.Log($"Foreground editor: {_currentEditorName}, project: {_currentProjectName}, hwnd: {_currentWindowHandle}");
        
        // STEP 2: If we already have cached data AND extraction is in progress, return cached data
        bool haveCachedData = _currentContentCache?.HasContent ?? false;
        
        lock (_lock)
        {
            if (_isExtracting)
            {
                _logger.LogDebug("Extraction in progress, returning cached data");
                return BuildKeywordList();
            }
        }
        
        // STEP 3: Try fast extraction using cached node paths (per-window)
        var fastResult = await TryFastExtractionAsync(process);
        if (fastResult != null)
        {
            // Fast extraction succeeded - update our cached data
            UpdateContentCache(fastResult);
            _lastExtraction = DateTime.UtcNow;
            _logger.LogDebug("Fast extraction: {Files} files, {Symbols} symbols", 
                fastResult.Tabs.Count + fastResult.ExplorerItems.Count, fastResult.Symbols.Count);
            return BuildKeywordList();
        }
        
        // STEP 4: Fast extraction failed (no cache or stale cache)
        // Return whatever cached data we have and kick off background refresh
        if (haveCachedData)
        {
            _logger.LogDebug("Cache miss, returning stale data and refreshing in background");
            // Kick off background refresh (don't await)
            _ = RefreshCacheInBackgroundAsync(process);
            return BuildKeywordList();
        }
        
        // STEP 5: No cached data at all - we need to do initial extraction
        // This only happens on first use - do it but with a short timeout
        _logger.LogInformation("First extraction for project '{Project}' - building cache", _currentProjectName);
        await RefreshCacheInBackgroundAsync(process);
        return BuildKeywordList();
    }
    
    /// <summary>
    /// Attempts fast extraction using cached node paths (per-window).
    /// Returns null if cache is missing or invalid (3 strikes).
    /// Handles PartiallyValid state by extracting with working paths and triggering background refresh.
    /// Should complete in ~50ms when cache is valid.
    /// </summary>
    private async Task<ExtractionResult?> TryFastExtractionAsync(Process process)
    {
        // Use the current window's path cache
        if (_currentPathCache == null)
        {
            _logger.LogDebug("No path cache for current window");
            CacheLogger.Log($"No path cache for window {_currentWindowHandle}");
            return null;
        }
        
        if (!_currentPathCache.IsComplete)
        {
            _logger.LogDebug("Path cache incomplete for current window");
            CacheLogger.Log($"Cache incomplete for window {_currentWindowHandle}");
            return null;
        }
        
        // Use the current foreground window handle (already verified)
        var hwnd = _currentWindowHandle;
        int hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref IID_IAccessible, out object accObj);
        if (hr != 0 || accObj == null)
        {
            CacheLogger.Log($"Failed to get IAccessible for window {hwnd}");
            return null;
        }
        
        var root = (IAccessible)accObj;
        var result = new ExtractionResult();
        var stopwatch = Stopwatch.StartNew();
        
        // Validate cache with 3-tier validation
        var status = ValidateCache(root);
        
        if (status == CacheStatus.Invalid)
        {
            // 3 strikes - clear cache and return null to trigger full refresh
            _currentPathCache = new PathCache();
            _windowPathCaches[_currentWindowHandle] = _currentPathCache;
            _logger.LogDebug("Cache invalidated after 3 failures, will do full refresh");
            return null;
        }
        
        if (status == CacheStatus.NotFound)
        {
            return null;
        }
        
        // Valid or PartiallyValid - extract with whatever paths work
        bool success = await Task.Run(() => TryExtractWithCache(root, result));
        
        stopwatch.Stop();
        
        if (success && (result.Tabs.Count > 0 || !string.IsNullOrEmpty(result.CodeContent)))
        {
            _logger.LogDebug("Fast extraction succeeded: {Time}ms", stopwatch.ElapsedMilliseconds);
            
            // Extract symbols from code BEFORE logging so we have accurate counts
            if (!string.IsNullOrEmpty(result.CodeContent))
            {
                result.Symbols = ExtractSymbols(result.CodeContent);
            }
            
            // Log with accurate file and symbol counts
            CacheLogger.LogExtraction("Cached", result.Tabs.Count + result.ExplorerItems.Count, 
                result.Symbols.Count, stopwatch.ElapsedMilliseconds);
            
            // If PartiallyValid, trigger background refresh to fix broken paths
            if (status == CacheStatus.PartiallyValid)
            {
                _logger.LogDebug("Cache partially valid, triggering background refresh");
                CacheLogger.LogBackground("Triggering refresh due to PartiallyValid cache");
                _ = RefreshCacheInBackgroundAsync(process);
            }
            
            return result;
        }
        
        _logger.LogDebug("Fast extraction failed: {Time}ms", stopwatch.ElapsedMilliseconds);
        CacheLogger.Log($"Fast extraction failed after {stopwatch.ElapsedMilliseconds}ms");
        return null;
    }
    
    /// <summary>
    /// Refreshes the path cache in the background by doing a full traversal.
    /// This updates the cached paths for the current window (per-window).
    /// </summary>
    private async Task RefreshCacheInBackgroundAsync(Process process)
    {
        // Prevent concurrent extractions
        lock (_lock)
        {
            if (_isExtracting)
                return;
            _isExtracting = true;
        }
        
        // Capture current state at start (in case foreground changes during traversal)
        var hwnd = _currentWindowHandle;
        var projectName = _currentProjectName;
        
        CacheLogger.LogBackground($"Starting full traversal for window {hwnd}, project '{projectName}'");
        
        try
        {
            int hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref IID_IAccessible, out object accObj);
            if (hr != 0 || accObj == null)
            {
                _logger.LogWarning("Could not get IAccessible for window {Hwnd}", hwnd);
                CacheLogger.LogBackground($"Failed to get IAccessible for window {hwnd}");
                return;
            }
            
            var root = (IAccessible)accObj;
            var result = new ExtractionResult();
            var pathsFound = new FoundPaths();
            var stopwatch = Stopwatch.StartNew();
            
            // Full traversal with path tracking - runs on thread pool
            await Task.Run(() => FullTraversalWithPathTracking(root, 0, result, pathsFound, 
                new List<int>(), new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
            
            stopwatch.Stop();
            _logger.LogDebug("Full traversal with path tracking: {Time}ms", stopwatch.ElapsedMilliseconds);
            CacheLogger.LogBackground($"Full traversal complete: {stopwatch.ElapsedMilliseconds}ms");
            
            // Save the paths we found to per-window cache (runtime only)
            if (pathsFound.TabContainerPath != null || pathsFound.CodeEditorPath != null)
            {
                var pathCache = new PathCache
                {
                    ProcessName = process.ProcessName,
                    TabContainerPath = pathsFound.TabContainerPath,
                    TabContainerDepth = pathsFound.TabContainerDepth,
                    ExplorerContainerPath = pathsFound.ExplorerContainerPath,
                    ExplorerContainerDepth = pathsFound.ExplorerContainerDepth,
                    CodeEditorPath = pathsFound.CodeEditorPath,
                    CodeEditorDepth = pathsFound.CodeEditorDepth,
                    CreatedAt = DateTime.UtcNow
                };
                
                _windowPathCaches[hwnd] = pathCache;
                
                // Update current if this is still the active window
                if (hwnd == _currentWindowHandle)
                {
                    _currentPathCache = pathCache;
                }
                
                _logger.LogInformation("Saved new path cache for window {Hwnd}", hwnd);
            }
            else
            {
                CacheLogger.LogBackground("No paths found during traversal");
            }
            
            // Extract symbols from code
            if (!string.IsNullOrEmpty(result.CodeContent))
            {
                result.Symbols = ExtractSymbols(result.CodeContent);
            }
            
            // Update per-project content cache
            if (result.Tabs.Count > 0 || result.ExplorerItems.Count > 0 || result.Symbols.Count > 0)
            {
                UpdateContentCache(result);
                _lastExtraction = DateTime.UtcNow;
                
                var contentCache = _currentContentCache;
                if (contentCache != null)
                {
                    _logger.LogInformation("Background extraction complete: {TabFiles} tab files, {ExplorerFiles} explorer files, {Symbols} symbols",
                        contentCache.TabFiles.Count, contentCache.ExplorerFiles.Count, contentCache.Symbols.Count);
                    
                    CacheLogger.LogExtraction("Background", contentCache.TotalFileCount, contentCache.Symbols.Count, 0);
                    
                    // Save to disk if we have a project name
                    if (!string.IsNullOrEmpty(projectName))
                    {
                        SaveProjectContentCache(projectName, contentCache);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background cache refresh failed");
            CacheLogger.Log($"Background refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isExtracting = false;
            }
        }
    }
    
    /// <summary>
    /// Helper class to track paths found during full traversal.
    /// </summary>
    private class FoundPaths
    {
        public int[]? TabContainerPath { get; set; }
        public int TabContainerDepth { get; set; }
        public int[]? ExplorerContainerPath { get; set; }
        public int ExplorerContainerDepth { get; set; }
        public int[]? CodeEditorPath { get; set; }
        public int CodeEditorDepth { get; set; }
    }
    
    /// <summary>
    /// Checks if a supported code editor is currently in the FOREGROUND.
    /// Uses fast GetForegroundWindow() check - no Process.GetProcesses() needed.
    /// </summary>
    public bool IsSupportedEditorActive()
    {
        return GetForegroundEditorWindow() != IntPtr.Zero;
    }
    
    /// <summary>
    /// Gets code context formatted for LLM prompts (Polish/Code Dictation).
    /// Returns file names and symbols for the model to use when correcting transcriptions.
    /// Maximum 500 total: 100 file names, 400 symbols.
    /// Only works when code editor is in FOREGROUND.
    /// </summary>
    public async Task<CodeContextForPrompt?> GetContextForPromptAsync()
    {
        // GetKeywordsForDeepgramAsync already checks for foreground editor internally
        await GetKeywordsForDeepgramAsync();
        
        if (_currentContentCache == null || !_currentContentCache.HasContent)
            return null;
        
        // Get all files from current project cache
        var allFiles = _currentContentCache.GetAllFiles();
        
        // Get prioritized lists (uncommon words first)
        var files = PrioritizeUncommonWords(allFiles)
            .Take(MAX_PROMPT_FILES)
            .ToList();
        
        var symbols = PrioritizeUncommonWords(_currentContentCache.Symbols)
            .Take(MAX_PROMPT_SYMBOLS)
            .ToList();
        
        return new CodeContextForPrompt
        {
            FileNames = files,
            Symbols = symbols
        };
    }
    
    /// <summary>
    /// Gets file names WITH extensions for @ mention detection and Tab tagging.
    /// Only file names should trigger Tab autocomplete in AI chats.
    /// Returns empty list if no code editor is in foreground.
    /// </summary>
    public List<string> GetFileNamesForMentions()
    {
        if (_currentContentCache == null)
            return new List<string>();
        
        // Get files from current project cache: tabs first (higher priority), then explorer
        var result = _currentContentCache.GetAllFiles();
        CacheLogger.Log($"GetFileNamesForMentions: {_currentContentCache.TabFiles.Count} tabs, {_currentContentCache.ExplorerFiles.Count} explorer -> {result.Count} total");
        return result;
    }
    
    /// <summary>
    /// Clears all cached data for the current project.
    /// </summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            // Clear current content cache
            if (_currentContentCache != null)
            {
                _currentContentCache.TabFiles.Clear();
                _currentContentCache.ExplorerFiles.Clear();
                _currentContentCache.Symbols.Clear();
            }
            
            // Clear current path cache
            if (_currentPathCache != null && _currentWindowHandle != IntPtr.Zero)
            {
                _windowPathCaches.Remove(_currentWindowHandle);
                _currentPathCache = new PathCache();
                _windowPathCaches[_currentWindowHandle] = _currentPathCache;
            }
            
            CacheLogger.Log("ClearCache: Cleared current project cache");
        }
        _lastExtraction = DateTime.MinValue;
    }
    
    /// <summary>
    /// Updates the file name queue with newly extracted files.
    /// Maintains separate queues for tabs and explorer files.
    /// </summary>
    /// <summary>
    /// Updates the current project's content cache with extracted results.
    /// </summary>
    private void UpdateContentCache(ExtractionResult result)
    {
        if (_currentContentCache == null)
            return;
        
        lock (_lock)
        {
            // Track all existing names for deduplication
            var existingNames = new HashSet<string>(
                _currentContentCache.TabFiles.Concat(_currentContentCache.ExplorerFiles), 
                StringComparer.OrdinalIgnoreCase);
            
            int tabsAdded = 0;
            int explorerAdded = 0;
            
            // Add new files from tabs (highest priority)
            foreach (var tab in result.Tabs)
            {
                var fileName = ExtractFileName(tab);
                if (!string.IsNullOrEmpty(fileName) && !existingNames.Contains(fileName))
                {
                    AddToList(_currentContentCache.TabFiles, fileName, MAX_FILE_QUEUE_SIZE / 2);
                    existingNames.Add(fileName);
                    tabsAdded++;
                }
            }
            
            // Add new files from explorer
            foreach (var item in result.ExplorerItems)
            {
                var fileName = ExtractFileName(item);
                if (!string.IsNullOrEmpty(fileName) && !existingNames.Contains(fileName))
                {
                    AddToList(_currentContentCache.ExplorerFiles, fileName, MAX_FILE_QUEUE_SIZE / 2);
                    existingNames.Add(fileName);
                    explorerAdded++;
                }
            }
            
            // Update symbols
            if (result.Symbols.Count > 0)
            {
                _currentContentCache.Symbols = result.Symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            
            CacheLogger.Log($"UpdateContentCache: Added {tabsAdded} tabs (total {_currentContentCache.TabFiles.Count}), {explorerAdded} explorer (total {_currentContentCache.ExplorerFiles.Count}), {_currentContentCache.Symbols.Count} symbols");
        }
    }
    
    /// <summary>
    /// Adds a file to a list at the front, maintaining max size.
    /// </summary>
    private void AddToList(List<string> list, string fileName, int maxSize)
    {
        // Insert at front (most recent)
        list.Insert(0, fileName);
        
        // Trim to max size
        while (list.Count > maxSize)
        {
            list.RemoveAt(list.Count - 1);
        }
    }
    
    private List<string> BuildKeywordList()
    {
        if (_currentContentCache == null)
            return new List<string>();
        
        // Combine tabs and explorer: tabs first (higher priority)
        var allFiles = _currentContentCache.GetAllFiles();
        
        CacheLogger.Log($"BuildKeywordList: {_currentContentCache.TabFiles.Count} tab files, {_currentContentCache.ExplorerFiles.Count} explorer files, {allFiles.Count} total unique");
        
        // Prioritize uncommon words - Deepgram already knows common English
        var files = PrioritizeUncommonWords(allFiles);
        var symbols = PrioritizeUncommonWords(_currentContentCache.Symbols);
        
        var keywords = new List<string>();
        
        int fileTarget = TARGET_FILE_KEYWORDS;
        int symbolTarget = TARGET_SYMBOL_KEYWORDS;
        
        // If files < 30, allow symbols to take more
        if (files.Count < TARGET_FILE_KEYWORDS)
        {
            symbolTarget = MAX_DEEPGRAM_KEYWORDS - files.Count;
        }
        
        // Add file keywords (without extension for better speech matching)
        foreach (var file in files.Take(fileTarget))
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(nameWithoutExt))
                keywords.Add(nameWithoutExt);
        }
        
        // Add symbol keywords
        keywords.AddRange(symbols.Take(symbolTarget));
        
        // If we still have space and have more files, add them
        int remaining = MAX_DEEPGRAM_KEYWORDS - keywords.Count;
        if (remaining > 0 && files.Count > fileTarget)
        {
            foreach (var file in files.Skip(fileTarget).Take(remaining))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(nameWithoutExt))
                    keywords.Add(nameWithoutExt);
            }
        }
        
        // Ensure uniqueness and proper formatting
        return keywords
            .Where(k => !string.IsNullOrWhiteSpace(k) && k.Length >= 2 && k.Length <= 50)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MAX_DEEPGRAM_KEYWORDS)
            .ToList();
    }
    
    /// <summary>
    /// Sorts keywords so uncommon words (not in common English dictionary) come first.
    /// </summary>
    private List<string> PrioritizeUncommonWords(List<string> words)
    {
        return words
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .OrderBy(w => EnglishDictionary.IsEnglishWordOrPhrase(w) ? 1 : 0)
            .ThenBy(w => w.Length < 4 ? 1 : 0) // Longer words first
            .ToList();
    }
    
    /// <summary>
    /// Extracts the file name with extension from a tab or explorer item name.
    /// </summary>
    private string? ExtractFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Handle VS Code/Cursor tab names with extra info like:
        // "file.py (Working Tree) (file.py)" or "file.py - Modified"
        // Extract just the filename part
        var cleaned = name.Trim();
        
        // If there's a space followed by parenthesis or dash, take the part before
        int spaceParenIdx = cleaned.IndexOf(" (", StringComparison.Ordinal);
        int spaceDashIdx = cleaned.IndexOf(" -", StringComparison.Ordinal);
        
        if (spaceParenIdx > 0)
            cleaned = cleaned.Substring(0, spaceParenIdx);
        else if (spaceDashIdx > 0)
            cleaned = cleaned.Substring(0, spaceDashIdx);

        // Remove path if present, keep the file name with extension
        var fileName = Path.GetFileName(cleaned);

        // Filter out invalid names
        if (string.IsNullOrEmpty(fileName) || fileName.Length < 2)
            return null;

        // Must have a valid code file extension
        if (!HasCodeFileExtension(fileName))
        {
            CacheLogger.Log($"ExtractFileName: '{name}' -> '{fileName}' rejected (no code extension)");
            return null;
        }

        CacheLogger.Log($"ExtractFileName: '{name}' -> '{fileName}'");
        return fileName;
    }
    
    /// <summary>
    /// Validates the cache with 3-tier validation:
    /// Tier 1: Tab container (most important)
    /// Tier 2: Explorer container
    /// Tier 3: Code editor (lenient - don't invalidate for empty files)
    /// 
    /// Uses 3-strikes rule: only invalidate cache after 3 consecutive failures.
    /// </summary>
    private CacheStatus ValidateCache(IAccessible root)
    {
        if (_currentPathCache == null)
            return CacheStatus.NotFound;
        
        bool tabsValid = false;
        bool explorerValid = false;
        bool codeValid = false;
        
        // Tier 1: Validate TabContainerPath (most important)
        if (_currentPathCache.TabContainerPath != null)
        {
            var tabContainer = NavigateToPath(root, _currentPathCache.TabContainerPath);
            if (tabContainer != null && ValidateContainerHasChildRole(tabContainer, ROLE_PAGETAB))
            {
                tabsValid = true;
            }
        }
        
        // Tier 2: Validate ExplorerContainerPath
        if (_currentPathCache.ExplorerContainerPath != null)
        {
            var explorerContainer = NavigateToPath(root, _currentPathCache.ExplorerContainerPath);
            if (explorerContainer != null && ValidateContainerHasChildRole(explorerContainer, ROLE_OUTLINEITEM))
            {
                explorerValid = true;
            }
        }
        
        // Tier 3: Code editor (very lenient - just check path exists and has children)
        // Note: CodeEditorPath points to the parent of the TEXT node, so we can SearchForCode from it
        if (_currentPathCache.CodeEditorPath != null)
        {
            var codeArea = NavigateToPath(root, _currentPathCache.CodeEditorPath);
            if (codeArea != null)
            {
                int childCount = 0;
                try { childCount = codeArea.accChildCount; } catch { }
                // If the path exists and has children, consider it valid
                // Actual code detection happens in SearchForCode
                if (childCount > 0)
                {
                    codeValid = true;
                }
            }
        }
        
        // Log validation details
        string details = $"Tabs={tabsValid}, Explorer={explorerValid}, Code={codeValid}";
        
        // Determine status based on tabs (most critical)
        if (tabsValid)
        {
            _currentPathCache.ValidationFailures = 0;
            _currentPathCache.LastValidated = DateTime.UtcNow;
            CacheLogger.LogValidation(CacheStatus.Valid, details);
            return CacheStatus.Valid;
        }
        
        // Tabs failed - apply 3-strikes rule
        _currentPathCache.ValidationFailures++;
        CacheLogger.Log($"Validation failure #{_currentPathCache.ValidationFailures} - {details}");
        
        if (_currentPathCache.ValidationFailures >= 3)
        {
            CacheLogger.LogValidation(CacheStatus.Invalid, $"{details} - 3 strikes, invalidating cache");
            return CacheStatus.Invalid;
        }
        
        CacheLogger.LogValidation(CacheStatus.PartiallyValid, $"{details} - Strike {_currentPathCache.ValidationFailures}");
        return CacheStatus.PartiallyValid;
    }
    
    private bool TryExtractWithCache(IAccessible root, ExtractionResult result)
    {
        if (_currentPathCache == null)
        {
            CacheLogger.Log("TryExtractWithCache: No cache available");
            return false;
        }
        
        CacheLogger.Log($"TryExtractWithCache: TabPath={_currentPathCache.TabContainerPath != null}, ExplorerPath={_currentPathCache.ExplorerContainerPath != null}, CodePath={_currentPathCache.CodeEditorPath != null}");
        
        bool anyValidPath = false;
        
        try
        {
            // Extract tabs - validate that the container actually has PAGETAB children
            if (_currentPathCache.TabContainerPath != null)
            {
                CacheLogger.Log($"TryExtractWithCache: Navigating to tab path [{string.Join(",", _currentPathCache.TabContainerPath)}]");
                var tabContainer = NavigateToPath(root, _currentPathCache.TabContainerPath);
                if (tabContainer != null)
                {
                    CacheLogger.Log("TryExtractWithCache: Tab container found, validating...");
                    // Validate: check if first child is a PAGETAB
                    if (ValidateContainerHasChildRole(tabContainer, ROLE_PAGETAB))
                    {
                        CacheLogger.Log("TryExtractWithCache: Tab container valid, extracting tabs...");
                        ExtractTabsFromContainer(tabContainer, result);
                        if (result.Tabs.Count > 0)
                            anyValidPath = true;
                        CacheLogger.Log($"TryExtractWithCache: Extracted {result.Tabs.Count} tabs");
                    }
                    else
                    {
                        CacheLogger.Log("TryExtractWithCache: Tab container INVALID - children are not PAGETABs");
                        _logger.LogDebug("Tab container cache invalid - children are not PAGETABs");
                        _currentPathCache.TabContainerPath = null; // Invalidate this path
                    }
                }
                else
                {
                    CacheLogger.Log("TryExtractWithCache: Could not navigate to tab container path");
                }
            }
            else
            {
                CacheLogger.Log("TryExtractWithCache: No tab container path in cache");
            }
            
            // Extract explorer items - validate that the container has OUTLINEITEM children
            if (_currentPathCache.ExplorerContainerPath != null)
            {
                CacheLogger.Log($"TryExtractWithCache: Navigating to explorer path [{string.Join(",", _currentPathCache.ExplorerContainerPath)}]");
                var explorerContainer = NavigateToPath(root, _currentPathCache.ExplorerContainerPath);
                if (explorerContainer != null)
                {
                    CacheLogger.Log("TryExtractWithCache: Explorer container found, validating...");
                    // Validate: check if first child is an OUTLINEITEM
                    if (ValidateContainerHasChildRole(explorerContainer, ROLE_OUTLINEITEM))
                    {
                        CacheLogger.Log("TryExtractWithCache: Explorer container valid, extracting items...");
                        ExtractExplorerItems(explorerContainer, result);
                        if (result.ExplorerItems.Count > 0)
                            anyValidPath = true;
                        CacheLogger.Log($"TryExtractWithCache: Extracted {result.ExplorerItems.Count} explorer items");
                    }
                    else
                    {
                        CacheLogger.Log("TryExtractWithCache: Explorer container INVALID - children are not OUTLINEITEMs");
                        _logger.LogDebug("Explorer container cache invalid - children are not OUTLINEITEMs");
                        _currentPathCache.ExplorerContainerPath = null; // Invalidate this path
                    }
                }
                else
                {
                    CacheLogger.Log("TryExtractWithCache: Could not navigate to explorer container path");
                }
            }
            else
            {
                CacheLogger.Log("TryExtractWithCache: No explorer container path in cache");
            }
            
            // Extract code using SearchForCode (recursive, more resilient)
            if (_currentPathCache.CodeEditorPath != null)
            {
                var codeArea = NavigateToPath(root, _currentPathCache.CodeEditorPath);
                if (codeArea != null)
                {
                    // Use recursive search from cached parent path
                    SearchForCode(codeArea, result, 0, 10);
                    if (!string.IsNullOrEmpty(result.CodeContent))
                    {
                        anyValidPath = true;
                    }
                }
            }
            
            // Fallback: search from tab container parent if code not found
            if (string.IsNullOrEmpty(result.CodeContent) && _currentPathCache.TabContainerPath != null && _currentPathCache.TabContainerPath.Length > 1)
            {
                var parentPath = _currentPathCache.TabContainerPath.Take(_currentPathCache.TabContainerPath.Length - 1).ToArray();
                var parent = NavigateToPath(root, parentPath);
                if (parent != null)
                {
                    SearchForCode(parent, result, 0, 15);
                    if (!string.IsNullOrEmpty(result.CodeContent))
                    {
                        anyValidPath = true;
                        _logger.LogDebug("Found code via fallback search from tab container parent");
                    }
                }
            }
            
            // Note: Cache invalidation and 3-strikes logic is now handled by ValidateCache()
            // in TryFastExtractionAsync. TryExtractWithCache just extracts with what works.
            
            // Log extraction attempt - note: symbols are extracted later in TryFastExtractionAsync
            bool hasCode = !string.IsNullOrEmpty(result.CodeContent);
            CacheLogger.Log($"TryExtract: {result.Tabs.Count + result.ExplorerItems.Count} files, hasCode={hasCode}, codeLen={result.CodeContent?.Length ?? 0}");
            
            return anyValidPath && (result.Tabs.Count > 0 || !string.IsNullOrEmpty(result.CodeContent));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache extraction failed");
            return false;
        }
    }
    
    /// <summary>
    /// Validates that a container has at least one child with the expected role.
    /// </summary>
    private bool ValidateContainerHasChildRole(IAccessible container, int expectedRole)
    {
        try
        {
            int childCount = container.accChildCount;
            if (childCount <= 0)
                return false;
            
            // Check up to first 5 children
            int toCheck = Math.Min(childCount, 5);
            object[] children = new object[toCheck];
            int obtained = 0;
            
            AccessibleChildren(container, 0, toCheck, children, out obtained);
            
            for (int i = 0; i < obtained; i++)
            {
                if (children[i] is IAccessible child)
                {
                    int role = GetRole(child);
                    if (role == expectedRole)
                        return true;
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Full traversal that also tracks the paths to important nodes for caching.
    /// </summary>
    private void FullTraversalWithPathTracking(IAccessible node, int depth, ExtractionResult result, 
        FoundPaths paths, List<int> currentPath, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || depth > 35)
            return;
        
        // Early exit if we have enough
        if (result.Tabs.Count >= 50 && result.ExplorerItems.Count >= 100 && !string.IsNullOrEmpty(result.CodeContent))
            return;
        
        int role = GetRole(node);
        int state = GetState(node);
        
        // Extract tabs and track path to tab container (parent of tabs)
        if (role == ROLE_PAGETAB && depth >= 20 && depth <= 35)
        {
            string? name = null;
            try { name = node.get_accName(0); } catch { }

            CacheLogger.Log($"FullTraversal: Found PAGETAB at depth {depth}, name='{name}'");

            if (!string.IsNullOrWhiteSpace(name) && name.Length < 150)
            {
                bool hasCodeExt = HasCodeFileExtension(name);
                CacheLogger.Log($"FullTraversal: Tab '{name}' hasCodeExt={hasCodeExt}");
                
                if (hasCodeExt)
                {
                    result.Tabs.Add(name);
                    CacheLogger.Log($"FullTraversal: Added tab '{name}' (total: {result.Tabs.Count})");

                    // Save path to tab container (parent - exclude last index)
                    if (paths.TabContainerPath == null && currentPath.Count > 0)
                    {
                        paths.TabContainerPath = currentPath.Take(currentPath.Count - 1).ToArray();
                        paths.TabContainerDepth = depth - 1;
                        CacheLogger.LogPathDiscovery("TabContainer", paths.TabContainerPath, depth - 1);
                    }
                }
            }
        }
        
        // Extract code content from editor and track path
        // KEY: The NAME property of the TEXT node contains the filename!
        if (role == ROLE_TEXT && depth >= 20 && depth <= 35)
        {
            bool isFocusable = (state & STATE_FOCUSABLE) != 0;
            bool isOffscreen = (state & STATE_OFFSCREEN) != 0;
            
            if (isFocusable && !isOffscreen)
            {
                string? name = null;
                string? value = null;
                try { name = node.get_accName(0); } catch { }
                try { value = node.get_accValue(0); } catch { }
                
                // The name contains the filename (e.g. "DictationOrchestrator.cs")
                // The value contains the actual code content
                bool hasFilename = HasCodeFileExtension(name);
                
                // Look for substantial text content (likely code)
                if (!string.IsNullOrEmpty(value) && value.Length > 100)
                {
                    bool looksLikeCode = value.Contains("{") || value.Contains("(") ||
                                          value.Contains("=") || value.Contains(";") ||
                                          value.Contains("def ") || value.Contains("class ") ||
                                          value.Contains("function ") || value.Contains("import ");
                    
                    // Accept if filename is valid OR content looks like code
                    if (hasFilename || looksLikeCode)
                    {
                        if (string.IsNullOrEmpty(result.CodeContent) || value.Length > result.CodeContent.Length)
                        {
                            result.CodeContent = value;
                            
                            // Store parent path (so SearchForCode can search siblings)
                            paths.CodeEditorPath = currentPath.Count > 0 
                                ? currentPath.Take(currentPath.Count - 1).ToArray()
                                : currentPath.ToArray();
                            paths.CodeEditorDepth = depth - 1;
                            CacheLogger.LogPathDiscovery("CodeEditor", paths.CodeEditorPath, depth);
                            CacheLogger.Log($"Found code: name='{name}' value.Length={value.Length}");
                        }
                    }
                }
            }
        }
        
        // Extract explorer container - look for GROUPING with OUTLINEITEM children
        // This is more reliable than looking for OUTLINEITEM directly because the
        // container path stays valid even when git pane is active (no OUTLINEITEMs visible)
        if (role == ROLE_GROUPING && depth >= 18 && depth <= 28 && paths.ExplorerContainerPath == null)
        {
            var firstChild = GetChildAt(node, 0);
            if (firstChild != null && GetRole(firstChild) == ROLE_OUTLINEITEM)
            {
                string? itemName = null;
                try { itemName = firstChild.get_accName(0); } catch { }
                
                // Verify it's file explorer, not git commits (which have ", " in name)
                if (!string.IsNullOrEmpty(itemName) && !itemName.Contains(", ") && itemName.Length < 100)
                {
                    paths.ExplorerContainerPath = currentPath.ToArray();
                    paths.ExplorerContainerDepth = depth;
                    CacheLogger.LogPathDiscovery("Explorer", paths.ExplorerContainerPath, depth);
                    
                    // Also extract items while we're here
                    ExtractExplorerItems(node, result);
                }
            }
        }
        
        // Also extract individual explorer items if we're in the right depth range
        if (role == ROLE_OUTLINEITEM && depth >= 20 && depth <= 30)
        {
            string? name = null;
            try { name = node.get_accName(0); } catch { }
            
            if (!string.IsNullOrWhiteSpace(name) && name.Length < 200 && !name.Contains(", "))
            {
                result.ExplorerItems.Add(name);
            }
        }
        
        // Recurse
        int childCount = 0;
        try { childCount = node.accChildCount; } catch { return; }
        
        if (childCount > 0 && childCount < 200)
        {
            object[] children = new object[childCount];
            int obtained = 0;
            
            try { AccessibleChildren(node, 0, childCount, children, out obtained); }
            catch { return; }
            
            for (int i = 0; i < obtained && !ct.IsCancellationRequested; i++)
            {
                if (children[i] is IAccessible child)
                {
                    currentPath.Add(i);
                    FullTraversalWithPathTracking(child, depth + 1, result, paths, currentPath, ct);
                    currentPath.RemoveAt(currentPath.Count - 1);
                }
            }
        }
    }
    
    private void ExtractTabsFromContainer(IAccessible container, ExtractionResult result)
    {
        int childCount = 0;
        try { childCount = container.accChildCount; } catch { return; }

        CacheLogger.Log($"ExtractTabs: Container has {childCount} children");

        if (childCount <= 0 || childCount > 200)
        {
            CacheLogger.Log($"ExtractTabs: Invalid child count {childCount}, skipping");
            return;
        }

        object[] children = new object[childCount];
        int obtained = 0;

        try { AccessibleChildren(container, 0, childCount, children, out obtained); }
        catch { return; }

        CacheLogger.Log($"ExtractTabs: Obtained {obtained} children");

        int tabsFound = 0;
        for (int i = 0; i < obtained; i++)
        {
            if (children[i] is IAccessible child && GetRole(child) == ROLE_PAGETAB)
            {
                string? name = null;
                try { name = child.get_accName(0); } catch { }

                if (!string.IsNullOrWhiteSpace(name) && name.Length < 150)
                {
                    result.Tabs.Add(name);
                    tabsFound++;
                    CacheLogger.Log($"ExtractTabs: Found tab '{name}'");
                }
            }
        }
        CacheLogger.Log($"ExtractTabs: Total {tabsFound} tabs extracted");
    }
    
    private void ExtractExplorerItems(IAccessible container, ExtractionResult result)
    {
        int childCount = 0;
        try { childCount = container.accChildCount; } catch { return; }
        
        if (childCount <= 0 || childCount > 500)
            return;
        
        object[] children = new object[childCount];
        int obtained = 0;
        
        try { AccessibleChildren(container, 0, childCount, children, out obtained); }
        catch { return; }
        
        for (int i = 0; i < obtained; i++)
        {
            if (children[i] is IAccessible child && GetRole(child) == ROLE_OUTLINEITEM)
            {
                string? name = null;
                try { name = child.get_accName(0); } catch { }
                
                if (!string.IsNullOrWhiteSpace(name) && name.Length < 200)
                    result.ExplorerItems.Add(name);
            }
        }
    }
    
    private void SearchForCode(IAccessible node, ExtractionResult result, int depth, int maxDepth)
    {
        if (depth > maxDepth || !string.IsNullOrEmpty(result.CodeContent))
            return;
        
        int role = GetRole(node);
        int state = GetState(node);
        
        if (role == ROLE_TEXT)
        {
            bool isFocusable = (state & STATE_FOCUSABLE) != 0;
            bool isOffscreen = (state & STATE_OFFSCREEN) != 0;
            
            if (isFocusable && !isOffscreen)
            {
                string? name = null;
                string? value = null;
                try { name = node.get_accName(0); } catch { }
                try { value = node.get_accValue(0); } catch { }
                
                // The name contains the filename, value contains the code
                bool hasFilename = HasCodeFileExtension(name);
                
                if (!string.IsNullOrEmpty(value) && value.Length > 100)
                {
                    bool looksLikeCode = value.Contains("{") || value.Contains("(") || 
                                          value.Contains("=") || value.Contains(";") ||
                                          value.Contains("def ") || value.Contains("class ") ||
                                          value.Contains("function ") || value.Contains("import ");
                    
                    // Accept if filename is valid OR content looks like code
                    if (hasFilename || looksLikeCode)
                    {
                        result.CodeContent = value;
                        CacheLogger.Log($"SearchForCode found: name='{name}', length={value.Length}");
                        return;
                    }
                }
            }
        }
        
        int childCount = 0;
        try { childCount = node.accChildCount; } catch { return; }
        
        if (childCount > 0 && childCount < 100)
        {
            object[] children = new object[childCount];
            int obtained = 0;
            
            try { AccessibleChildren(node, 0, childCount, children, out obtained); }
            catch { return; }
            
            for (int i = 0; i < obtained; i++)
            {
                if (children[i] is IAccessible child)
                {
                    SearchForCode(child, result, depth + 1, maxDepth);
                    if (!string.IsNullOrEmpty(result.CodeContent))
                        return;
                }
            }
        }
    }
    
    private List<string> ExtractSymbols(string code)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Common reserved words to exclude
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "return",
            "try", "catch", "finally", "throw", "throws", "new", "delete", "typeof", "instanceof",
            "true", "false", "null", "undefined", "this", "self", "super", "base", "class", "interface",
            "struct", "enum", "public", "private", "protected", "internal", "static", "final", "const",
            "let", "var", "function", "def", "async", "await", "yield", "import", "export", "from",
            "extends", "implements", "abstract", "virtual", "override", "readonly", "get", "set",
            "and", "or", "not", "in", "is", "as", "with", "lambda", "pass", "raise", "except",
            "print", "input", "open", "close", "read", "write", "append", "len", "str", "int",
            "float", "bool", "list", "dict", "set", "tuple", "type", "object", "none", "void"
        };
        
        var patterns = new[]
        {
            // Class, interface, struct, enum definitions
            @"(?:class|interface|struct|enum)\s+(\w+)",
            // Function/method definitions
            @"(?:def|function|func|fn)\s+(\w+)",
            // C#/Java style method signatures
            @"(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?(?:override\s+)?[\w<>\[\],\s]+\s+(\w+)\s*\(",
            // Variable declarations with keywords
            @"(?:const|let|var|val)\s+(\w+)\s*[=:]",
            // Python/Ruby style assignments
            @"^\s*([a-zA-Z_]\w*)\s*=\s*[^=]",
            // Attribute/property access after self/this/cls
            @"(?:self|this|cls)\s*\.\s*(\w+)",
            // Method calls after dot
            @"\.\s*([a-zA-Z_]\w*)\s*\(",
            // Property access after dot
            @"\.\s*([a-zA-Z_]\w+)",
            // Type annotations in Python
            @":\s*([A-Z][a-zA-Z0-9_]*)",
            // Type hints and generics
            @"<\s*([A-Z][a-zA-Z0-9_]*)",
            // Decorators in Python
            @"@([a-zA-Z_]\w+)",
            // Import statements
            @"(?:from|import)\s+([a-zA-Z_][\w\.]*)",
            // Parameter names in function definitions
            @"\(\s*([a-zA-Z_]\w*)\s*[,:=\)]",
            // CamelCase/PascalCase identifiers
            @"\b([A-Z][a-z]+(?:[A-Z][a-z]+)+)\b",
            // snake_case identifiers
            @"\b([a-z][a-z0-9]*(?:_[a-z0-9]+)+)\b",
        };
        
        foreach (var pattern in patterns)
        {
            try
            {
                foreach (Match m in Regex.Matches(code, pattern, RegexOptions.Multiline))
                {
                    if (m.Groups.Count > 1)
                    {
                        var symbol = m.Groups[1].Value;
                        if (symbol.Length >= 3 && 
                            !reserved.Contains(symbol) && 
                            !symbol.All(char.IsUpper) &&
                            !symbol.All(char.IsDigit) &&
                            symbol.Any(char.IsLetter))
                        {
                            symbols.Add(symbol);
                        }
                    }
                }
            }
            catch { }
        }
        
        return symbols.ToList();
    }
    
    private IAccessible? NavigateToPath(IAccessible root, int[]? path)
    {
        if (path == null)
            return null;
        
        var current = root;
        
        foreach (int childIndex in path)
        {
            int childCount = 0;
            try { childCount = current.accChildCount; } catch { return null; }
            
            if (childIndex >= childCount)
                return null;
            
            // Use reusable buffer if possible, otherwise allocate
            object[] children;
            if (childCount <= MAX_CHILDREN_BUFFER)
            {
                children = _childBuffer;
                Array.Clear(children, 0, childCount); // Clear only what we need
            }
            else
            {
                children = new object[childCount]; // Fallback for large child counts
            }
            
            int obtained = 0;
            try { AccessibleChildren(current, 0, childCount, children, out obtained); }
            catch { return null; }
            
            if (childIndex >= obtained || children[childIndex] is not IAccessible child)
                return null;
            
            current = child;
        }
        
        return current;
    }
    
    /// <summary>
    /// Gets a child at a specific index from a parent node.
    /// </summary>
    private IAccessible? GetChildAt(IAccessible parent, int index)
    {
        int childCount = 0;
        try { childCount = parent.accChildCount; } catch { return null; }
        
        if (index >= childCount || childCount <= 0)
            return null;
        
        object[] children = new object[childCount];
        int obtained = 0;
        
        try { AccessibleChildren(parent, 0, childCount, children, out obtained); }
        catch { return null; }
        
        if (index >= obtained)
            return null;
        
        return children[index] as IAccessible;
    }

    private bool HasCodeFileExtension(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        
        var extensions = new[] {
            // Common languages
            ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".go", ".rs", ".rb", ".php",
            ".swift", ".kt", ".scala", ".c", ".cpp", ".h", ".hpp", ".m", ".mm",
            // Web
            ".html", ".css", ".scss", ".sass", ".less", ".vue", ".svelte", ".astro",
            // Data/Config
            ".json", ".yaml", ".yml", ".xml", ".toml", ".ini", ".env", ".cfg",
            // Notebooks/Docs
            ".ipynb", ".md", ".mdx", ".rst", ".txt",
            // Scripts/Shell
            ".sh", ".bash", ".zsh", ".ps1", ".bat", ".cmd",
            // Other
            ".sql", ".graphql", ".proto", ".dockerfile", ".makefile", ".mk",
            ".tf", ".hcl", ".r", ".jl", ".lua", ".dart", ".ex", ".exs", ".erl",
            ".xaml", ".csproj", ".sln", ".gradle", ".pom"
        };
        
        return extensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
    
    private int GetRole(IAccessible node)
    {
        try
        {
            var roleObj = node.get_accRole(0);
            return roleObj is int r ? r : 0;
        }
        catch { return 0; }
    }
    
    private int GetState(IAccessible node)
    {
        try
        {
            var stateObj = node.get_accState(0);
            return stateObj is int s ? s : 0;
        }
        catch { return 0; }
    }
    
    /// <summary>
    /// Dumps the entire accessibility tree to a file for diagnostic purposes.
    /// Only runs when EnableDiagnostics is true.
    /// </summary>
    public async Task DumpAccessibilityTreeAsync()
    {
        if (!EnableDiagnostics)
        {
            _logger.LogDebug("DumpAccessibilityTreeAsync skipped - diagnostics disabled");
            return;
        }
        
        if (!SwitchToCurrentWindowContext())
        {
            CacheLogger.Log("DumpTree: No supported editor in foreground");
            return;
        }
        
        var process = GetCurrentEditorProcess();
        if (process == null)
        {
            CacheLogger.Log("DumpTree: No supported editor process found");
            return;
        }
        
        var hwnd = _currentWindowHandle;
        int hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref IID_IAccessible, out object accObj);
        if (hr != 0 || accObj == null)
        {
            CacheLogger.Log("DumpTree: Failed to get IAccessible");
            return;
        }
        
        var root = (IAccessible)accObj;
        var dumpPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WisperFlow", "accessibility_tree_dump.txt");
        
        using var writer = new StreamWriter(dumpPath, false);
        await writer.WriteLineAsync($"Accessibility Tree Dump - {DateTime.Now}");
        await writer.WriteLineAsync($"Process: {process.ProcessName} (PID: {process.Id})");
        await writer.WriteLineAsync(new string('=', 80));
        await writer.WriteLineAsync();
        
        await Task.Run(() => DumpNode(root, writer, 0, new List<int>(), 40));
        
        CacheLogger.Log($"DumpTree: Written to {dumpPath}");
        _logger.LogInformation("Accessibility tree dumped to {Path}", dumpPath);
    }
    
    private void DumpNode(IAccessible node, StreamWriter writer, int depth, List<int> path, int maxDepth)
    {
        if (depth > maxDepth) return;
        
        try
        {
            int role = GetRole(node);
            int state = GetState(node);
            string? name = null;
            string? value = null;
            
            try { name = node.get_accName(0); } catch { }
            try { value = node.get_accValue(0); } catch { }
            
            string indent = new string(' ', depth * 2);
            string pathStr = path.Count > 0 ? $"[{string.Join(",", path)}]" : "[root]";
            
            string roleName = role switch
            {
                37 => "PAGETAB",
                42 => "TEXT",
                36 => "OUTLINEITEM",
                20 => "GROUPING",
                9 => "WINDOW",
                10 => "CLIENT",
                33 => "PANE",
                25 => "LIST",
                34 => "LISTITEM",
                40 => "PUSHBUTTON",
                28 => "MENUBAR",
                11 => "MENUITEM",
                39 => "TOOLBAR",
                _ => $"ROLE_{role}"
            };
            
            bool isFocusable = (state & STATE_FOCUSABLE) != 0;
            bool isOffscreen = (state & STATE_OFFSCREEN) != 0;
            string stateFlags = $"Foc={isFocusable},Off={isOffscreen}";
            
            // Truncate values for readability
            string displayName = name?.Length > 60 ? name.Substring(0, 60) + "..." : name ?? "(null)";
            string displayValue = value?.Length > 100 ? $"({value.Length} chars: {value.Substring(0, 80)}...)" : 
                                  value != null ? $"({value.Length} chars)" : "(null)";
            
            // Mark interesting nodes
            string marker = "";
            if (role == 37) marker = " *** TAB ***";
            if (role == 36) marker = " *** OUTLINEITEM ***";
            if (role == 42 && isFocusable && !isOffscreen && (value?.Length ?? 0) > 100) marker = " *** CODE? ***";
            if (role == 20) marker = " *** GROUPING ***";
            
            writer.WriteLine($"{indent}{pathStr} {roleName} {stateFlags} Name=\"{displayName}\" Value={displayValue}{marker}");
            
            // If this looks like code, log it
            if (role == 42 && (value?.Length ?? 0) > 100)
            {
                bool looksLikeCode = value!.Contains("{") || value.Contains("(") || 
                                     value.Contains("=") || value.Contains(";") ||
                                     value.Contains("def ") || value.Contains("class ");
                if (looksLikeCode)
                {
                    writer.WriteLine($"{indent}  ^^ LOOKS LIKE CODE! First 200 chars: {value.Substring(0, Math.Min(200, value.Length))}");
                }
            }
            
            int childCount = 0;
            try { childCount = node.accChildCount; } catch { return; }
            
            if (childCount > 0 && childCount < 200)
            {
                object[] children = new object[childCount];
                int obtained = 0;
                
                try { AccessibleChildren(node, 0, childCount, children, out obtained); }
                catch { return; }
                
                for (int i = 0; i < obtained; i++)
                {
                    if (children[i] is IAccessible child)
                    {
                        var childPath = new List<int>(path) { i };
                        DumpNode(child, writer, depth + 1, childPath, maxDepth);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            writer.WriteLine($"ERROR at depth {depth}: {ex.Message}");
        }
    }
    
    public void Dispose()
    {
        // Clear all runtime caches
        _windowPathCaches.Clear();
        _projectContentCaches.Clear();
        _knownEditorWindows.Clear();
        
        _currentPathCache = null;
        _currentContentCache = null;
        _currentWindowHandle = IntPtr.Zero;
        _currentProjectName = null;
    }
    
    #region P/Invoke
    
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    
    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(IAccessible paccContainer, int iChildStart,
        int cChildren, [Out] object[] rgvarChildren, out int pcObtained);
    
    private const uint OBJID_CLIENT = 0xFFFFFFFC;
    private static Guid IID_IAccessible = new("618736e0-3c3d-11cf-810c-00aa00389b71");
    
    private const int ROLE_PAGETAB = 37;
    private const int ROLE_TEXT = 42;
    private const int ROLE_OUTLINEITEM = 36;
    private const int ROLE_GROUPING = 20;
    private const int STATE_FOCUSABLE = 0x00100000;
    private const int STATE_OFFSCREEN = 0x00010000;
    
    #endregion
}

/// <summary>
/// Result of code context extraction.
/// </summary>
internal class ExtractionResult
{
    public List<string> Tabs { get; set; } = new();
    public List<string> ExplorerItems { get; set; } = new();
    public string? CodeContent { get; set; }
    public List<string> Symbols { get; set; } = new();
}

/// <summary>
/// Code context formatted for LLM prompts.
/// </summary>
public class CodeContextForPrompt
{
    public List<string> FileNames { get; set; } = new();
    public List<string> Symbols { get; set; } = new();
    
    /// <summary>
    /// Gets the context formatted as a string for inclusion in prompts.
    /// </summary>
    public string ToPromptString()
    {
        if (FileNames.Count == 0 && Symbols.Count == 0)
            return "";
        
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("CODE CONTEXT (from active code editor):");
        
        if (FileNames.Count > 0)
        {
            sb.AppendLine($"File names ({FileNames.Count}): {string.Join(", ", FileNames.Take(50))}");
            if (FileNames.Count > 50)
                sb.AppendLine($"  ... and {FileNames.Count - 50} more");
        }
        
        if (Symbols.Count > 0)
        {
            sb.AppendLine($"Symbols/variables ({Symbols.Count}): {string.Join(", ", Symbols.Take(100))}");
            if (Symbols.Count > 100)
                sb.AppendLine($"  ... and {Symbols.Count - 100} more");
        }
        
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: If the transcription contains words that sound similar to these file names or symbols,");
        sb.AppendLine("correct them to match the exact spelling from the list above.");
        sb.AppendLine();
        sb.AppendLine("FILE NAME TAGGING:");
        sb.AppendLine("- When the user mentions a FILE NAME, prefix it with @ (e.g., @constants.py, @pipelines.yaml)");
        sb.AppendLine("- File names typically have extensions like .py, .cs, .js, .ts, .json, .yaml, etc.");
        sb.AppendLine();
        sb.AppendLine("SYMBOL/VARIABLE FORMATTING:");
        sb.AppendLine("- Format symbols and variables correctly based on the code context (e.g., is_eligible, labels_present)");
        sb.AppendLine("- Do NOT prefix symbols or variables with @ - only file names get the @ prefix");
        
        return sb.ToString();
    }
}

/// <summary>
/// Per-project content cache for files and symbols.
/// Persisted to disk for fast restoration across app restarts.
/// </summary>
internal class ProjectContentCache
{
    public List<string> TabFiles { get; set; } = new();
    public List<string> ExplorerFiles { get; set; } = new();
    public List<string> Symbols { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Gets all files (tabs first, then explorer) without duplicates.
    /// </summary>
    public List<string> GetAllFiles()
    {
        return TabFiles.Concat(ExplorerFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    
    /// <summary>
    /// Total file count across both queues.
    /// </summary>
    public int TotalFileCount => TabFiles.Count + ExplorerFiles.Count;
    
    /// <summary>
    /// Checks if this cache has any content.
    /// </summary>
    public bool HasContent => TabFiles.Count > 0 || ExplorerFiles.Count > 0 || Symbols.Count > 0;
}
