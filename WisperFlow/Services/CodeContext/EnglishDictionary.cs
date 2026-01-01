using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace WisperFlow.Services.CodeContext;

/// <summary>
/// Offline English word checker using Hunspell dictionary.
/// Used to filter out common English words from Deepgram keywords,
/// since Deepgram already knows common English and we want to boost technical terms.
/// </summary>
public static class EnglishDictionary
{
    private static WordList? _dictionary;
    private static readonly object _lock = new();
    private static bool _initialized;
    private static bool _initFailed;
    
    // Dictionary download URLs (LibreOffice dictionaries)
    private const string DicUrl = "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.dic";
    private const string AffUrl = "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.aff";
    
    /// <summary>
    /// Checks if a single word is a standard English word.
    /// Returns true if the word is in the English dictionary.
    /// </summary>
    public static bool IsEnglishWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 3)
            return false;
        
        EnsureInitialized();
        
        if (_dictionary == null)
            return false; // If dictionary failed to load, assume not English (safer for our use case)
        
        return _dictionary.Check(word.ToLowerInvariant());
    }
    
    /// <summary>
    /// Checks if a compound word (CamelCase, snake_case) contains only English words.
    /// Returns true if ALL parts are English words.
    /// </summary>
    public static bool IsEnglishWordOrPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        
        var parts = SplitIntoParts(text);
        
        // If no meaningful parts, treat as non-English
        if (parts.Count == 0)
            return false;
        
        // Check if ALL parts are English words
        foreach (var part in parts)
        {
            if (part.Length < 3)
                continue; // Skip short parts
            
            if (!IsEnglishWord(part))
                return false; // Found a non-English part
        }
        
        return true;
    }
    
    /// <summary>
    /// Splits a compound identifier into word parts.
    /// Handles CamelCase, snake_case, kebab-case, and dot.notation.
    /// </summary>
    private static List<string> SplitIntoParts(string text)
    {
        var parts = new List<string>();
        
        // Split by common separators
        var segments = text.Split(new[] { '_', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var segment in segments)
        {
            // Split CamelCase: "getUserName" -> ["get", "User", "Name"]
            var camelParts = Regex.Split(segment, @"(?<!^)(?=[A-Z])");
            foreach (var part in camelParts)
            {
                if (!string.IsNullOrEmpty(part) && part.Length >= 2)
                {
                    parts.Add(part.ToLowerInvariant());
                }
            }
        }
        
        return parts;
    }
    
    /// <summary>
    /// Ensures the dictionary is loaded. Downloads if necessary.
    /// </summary>
    private static void EnsureInitialized()
    {
        if (_initialized || _initFailed)
            return;
        
        lock (_lock)
        {
            if (_initialized || _initFailed)
                return;
            
            try
            {
                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WisperFlow", "Dictionary");
                
                Directory.CreateDirectory(cacheDir);
                
                var dicPath = Path.Combine(cacheDir, "en_US.dic");
                var affPath = Path.Combine(cacheDir, "en_US.aff");
                
                // Download dictionary files if not present
                if (!File.Exists(dicPath) || !File.Exists(affPath))
                {
                    DownloadDictionaryFiles(dicPath, affPath);
                }
                
                // Load the dictionary
                if (File.Exists(dicPath) && File.Exists(affPath))
                {
                    _dictionary = WordList.CreateFromFiles(dicPath, affPath);
                    _initialized = true;
                }
                else
                {
                    _initFailed = true;
                }
            }
            catch
            {
                _initFailed = true;
            }
        }
    }
    
    /// <summary>
    /// Downloads dictionary files from LibreOffice repository.
    /// </summary>
    private static void DownloadDictionaryFiles(string dicPath, string affPath)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        
        try
        {
            // Download .dic file
            var dicContent = client.GetStringAsync(DicUrl).GetAwaiter().GetResult();
            File.WriteAllText(dicPath, dicContent);
            
            // Download .aff file
            var affContent = client.GetStringAsync(AffUrl).GetAwaiter().GetResult();
            File.WriteAllText(affPath, affContent);
        }
        catch
        {
            // Clean up partial downloads
            try { File.Delete(dicPath); } catch { }
            try { File.Delete(affPath); } catch { }
            throw;
        }
    }
}
