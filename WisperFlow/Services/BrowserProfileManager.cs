using System.IO;

namespace WisperFlow.Services;

/// <summary>
/// Manages persistent WebView2 browser profiles for AI providers.
/// Each provider has its own profile folder to maintain separate sessions.
/// </summary>
public static class BrowserProfileManager
{
    private static readonly string ProfilesBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WisperFlow",
        "Profiles"
    );

    /// <summary>
    /// Gets the shared profile folder path for all AI providers.
    /// Using a single profile allows instant switching between providers.
    /// Creates the folder if it doesn't exist.
    /// </summary>
    public static string GetProfilePath(string provider = "")
    {
        // Use a single shared profile for all providers
        // This allows instant switching without WebView2 reinitialization
        var profilePath = Path.Combine(ProfilesBasePath, "Shared");
        
        if (!Directory.Exists(profilePath))
        {
            Directory.CreateDirectory(profilePath);
        }
        
        return profilePath;
    }

    /// <summary>
    /// Clears all browsing data (signs out of all providers).
    /// </summary>
    public static void ClearProfile(string provider = "")
    {
        // Clear the shared profile (affects all providers)
        var profilePath = Path.Combine(ProfilesBasePath, "Shared");
        
        if (Directory.Exists(profilePath))
        {
            try
            {
                Directory.Delete(profilePath, recursive: true);
            }
            catch (IOException)
            {
                // Profile might be in use, will be cleared on next restart
            }
        }
    }

    /// <summary>
    /// Gets the home URL for a specific AI provider.
    /// </summary>
    public static string GetProviderUrl(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "chatgpt" => "https://chat.openai.com/",
            "gemini" => "https://gemini.google.com/app",
            _ => "https://chat.openai.com/"
        };
    }

    private static string SanitizeProviderName(string provider)
    {
        // Remove any invalid path characters
        var invalid = Path.GetInvalidFileNameChars();
        return new string(provider.Where(c => !invalid.Contains(c)).ToArray());
    }
}
