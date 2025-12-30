using System.Text.Json.Serialization;

namespace WisperFlow.Models;

/// <summary>
/// Application settings model persisted to JSON.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// The modifier keys for the hotkey (e.g., Ctrl+Win).
    /// </summary>
    public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Win;

    /// <summary>
    /// The main key for the hotkey.
    /// </summary>
    public int HotkeyKey { get; set; } = 0; // 0 means use modifiers only with release detection

    /// <summary>
    /// Selected microphone device ID. Empty = default device.
    /// </summary>
    public string MicrophoneDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Language code for transcription (e.g., "en", "auto").
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Whether to polish/clean the transcript before inserting.
    /// </summary>
    public bool PolishOutput { get; set; } = true;

    /// <summary>
    /// Notes mode (more aggressive cleanup) vs typing mode (minimal changes).
    /// </summary>
    public bool NotesMode { get; set; } = false;

    /// <summary>
    /// Whether to run at Windows startup.
    /// </summary>
    public bool LaunchAtStartup { get; set; } = false;

    /// <summary>
    /// Whether the hotkey is currently enabled.
    /// </summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Maximum recording duration in seconds.
    /// </summary>
    public int MaxRecordingDurationSeconds { get; set; } = 120;

    /// <summary>
    /// API key storage method.
    /// </summary>
    public ApiKeyStorageMethod ApiKeyStorage { get; set; } = ApiKeyStorageMethod.EnvironmentVariable;

    /// <summary>
    /// Custom prompt/vocabulary hint for Whisper.
    /// </summary>
    public string CustomPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Selected transcription model ID.
    /// </summary>
    public string TranscriptionModelId { get; set; } = "openai-whisper";

    /// <summary>
    /// Selected polish/LLM model ID.
    /// </summary>
    public string PolishModelId { get; set; } = "openai-gpt4o-mini";

    // ===== Command Mode Settings =====
    
    /// <summary>
    /// The modifier keys for command mode hotkey (default: Ctrl+Win+Alt).
    /// </summary>
    public HotkeyModifiers CommandHotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Win | HotkeyModifiers.Alt;

    /// <summary>
    /// Whether command mode is enabled.
    /// </summary>
    public bool CommandModeEnabled { get; set; } = true;

    /// <summary>
    /// The AI service to query when no text is selected (ChatGPT, Gemini, Perplexity).
    /// </summary>
    public string CommandModeSearchEngine { get; set; } = "ChatGPT";

    // ===== Code Dictation Mode Settings =====
    
    /// <summary>
    /// Whether code dictation mode is enabled.
    /// </summary>
    public bool CodeDictationEnabled { get; set; } = true;

    /// <summary>
    /// The modifier keys for code dictation hotkey (default: Ctrl+Shift).
    /// </summary>
    public HotkeyModifiers CodeDictationHotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Shift;

    /// <summary>
    /// Selected model for code dictation (3B+ models only).
    /// </summary>
    public string CodeDictationModelId { get; set; } = "qwen2.5-3b";

    /// <summary>
    /// Programming language for code dictation output.
    /// </summary>
    public string CodeDictationLanguage { get; set; } = "python";
}

/// <summary>
/// Hotkey modifier flags matching Windows virtual key modifiers.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// How the API key is stored.
/// </summary>
public enum ApiKeyStorageMethod
{
    EnvironmentVariable,
    CredentialManager
}

