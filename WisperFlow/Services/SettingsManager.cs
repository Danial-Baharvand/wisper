using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WisperFlow.Models;

namespace WisperFlow.Services;

/// <summary>
/// Manages application settings persistence to JSON file in AppData.
/// </summary>
public class SettingsManager
{
    private readonly ILogger<SettingsManager> _logger;
    private readonly string _settingsFilePath;
    private AppSettings _currentSettings;
    private readonly JsonSerializerOptions _jsonOptions;

    private const string AppName = "WisperFlow";
    private const string SettingsFileName = "settings.json";
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public AppSettings CurrentSettings => _currentSettings;

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsManager(ILogger<SettingsManager> logger)
    {
        _logger = logger;
        
        // Settings stored in %APPDATA%\WisperFlow\settings.json
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName);
        
        Directory.CreateDirectory(appDataPath);
        _settingsFilePath = Path.Combine(appDataPath, SettingsFileName);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        _currentSettings = new AppSettings();
    }

    /// <summary>
    /// Loads settings from disk or returns defaults.
    /// </summary>
    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                if (settings != null)
                {
                    _currentSettings = settings;
                    _logger.LogInformation("Settings loaded from {Path}", _settingsFilePath);
                    
                    // Migration: If PolishModelId was "polish-disabled", migrate to new approach
                    if (settings.PolishModelId == "polish-disabled")
                    {
                        _logger.LogInformation("Migrating polish-disabled setting: disabling PolishOutput and setting default model");
                        settings.PolishOutput = false;  // Disable polishing via the checkbox
                        settings.PolishModelId = "openai-gpt4o-mini";  // Set to default valid model
                        SaveSettings(settings);  // Persist the migration
                    }
                    
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings, using defaults");
        }

        _currentSettings = new AppSettings();
        _logger.LogInformation("Using default settings");
        return _currentSettings;
    }

    /// <summary>
    /// Saves settings to disk.
    /// </summary>
    public void SaveSettings(AppSettings settings)
    {
        try
        {
            _currentSettings = settings;
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(_settingsFilePath, json);
            _logger.LogInformation("Settings saved to {Path}", _settingsFilePath);

            // Handle startup setting
            SetStartupEnabled(settings.LaunchAtStartup);

            SettingsChanged?.Invoke(this, settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            throw;
        }
    }

    /// <summary>
    /// Updates a single setting and saves.
    /// </summary>
    public void UpdateSetting(Action<AppSettings> updateAction)
    {
        updateAction(_currentSettings);
        SaveSettings(_currentSettings);
    }

    /// <summary>
    /// Enables or disables launch at Windows startup.
    /// </summary>
    private void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            if (key == null)
            {
                _logger.LogWarning("Could not open startup registry key");
                return;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                    _logger.LogInformation("Added to Windows startup");
                }
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                _logger.LogInformation("Removed from Windows startup");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update startup setting");
        }
    }

    /// <summary>
    /// Gets the list of available languages for transcription.
    /// </summary>
    public static IReadOnlyList<(string Code, string Name)> GetAvailableLanguages()
    {
        return new List<(string, string)>
        {
            ("auto", "Auto-detect"),
            ("en", "English"),
            ("es", "Spanish"),
            ("fr", "French"),
            ("de", "German"),
            ("it", "Italian"),
            ("pt", "Portuguese"),
            ("nl", "Dutch"),
            ("pl", "Polish"),
            ("ru", "Russian"),
            ("ja", "Japanese"),
            ("ko", "Korean"),
            ("zh", "Chinese"),
            ("ar", "Arabic"),
            ("hi", "Hindi"),
            ("tr", "Turkish"),
            ("vi", "Vietnamese"),
            ("th", "Thai"),
            ("sv", "Swedish"),
            ("da", "Danish"),
            ("no", "Norwegian"),
            ("fi", "Finnish"),
            ("cs", "Czech"),
            ("el", "Greek"),
            ("he", "Hebrew"),
            ("hu", "Hungarian"),
            ("id", "Indonesian"),
            ("ms", "Malay"),
            ("ro", "Romanian"),
            ("sk", "Slovak"),
            ("uk", "Ukrainian")
        };
    }
}

