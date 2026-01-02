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
    /// Custom prompt for typing mode polish. Empty = use default.
    /// </summary>
    public string CustomTypingPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Custom prompt for notes mode polish. Empty = use default.
    /// </summary>
    public string CustomNotesPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Whether to run at Windows startup.
    /// </summary>
    public bool LaunchAtStartup { get; set; } = false;

    /// <summary>
    /// Whether the hotkey is currently enabled.
    /// </summary>
    public bool HotkeyEnabled { get; set; } = true;


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

    /// <summary>
    /// Selected LLM model for command mode (separate from polish model).
    /// This model handles transform and generate commands.
    /// </summary>
    public string CommandModeModelId { get; set; } = "openai-gpt4o-mini";

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

    // ===== Deepgram Settings =====
    
    /// <summary>
    /// Use streaming mode for Deepgram (real-time partial results).
    /// </summary>
    public bool DeepgramStreaming { get; set; } = false;

    /// <summary>
    /// Enable smart formatting (numbers, dates, currency).
    /// </summary>
    public bool DeepgramSmartFormat { get; set; } = false;

    /// <summary>
    /// Enable automatic punctuation.
    /// </summary>
    public bool DeepgramPunctuate { get; set; } = true;

    /// <summary>
    /// Enable speaker diarization (identify different speakers).
    /// </summary>
    public bool DeepgramDiarize { get; set; } = false;

    /// <summary>
    /// Enable utterance detection (split into sentences).
    /// </summary>
    public bool DeepgramUtterances { get; set; } = false;

    /// <summary>
    /// Enable paragraph detection.
    /// </summary>
    public bool DeepgramParagraphs { get; set; } = false;

    /// <summary>
    /// Enable filler word detection ("um", "uh").
    /// </summary>
    public bool DeepgramFillerWords { get; set; } = false;

    /// <summary>
    /// Keywords to boost recognition (comma-separated).
    /// </summary>
    public string DeepgramKeywords { get; set; } = string.Empty;

    /// <summary>
    /// Profanity filter mode: "false", "true" (mask), or "strict" (remove).
    /// </summary>
    public string DeepgramProfanityFilter { get; set; } = "false";

    /// <summary>
    /// Enable dictation mode for better entity recognition.
    /// </summary>
    public bool DeepgramDictation { get; set; } = false;

    /// <summary>
    /// Convert written numbers to numerals ("five" → "5").
    /// </summary>
    public bool DeepgramNumerals { get; set; } = false;

    /// <summary>
    /// Endpointing in milliseconds (how long to wait before finalizing).
    /// Lower = faster streaming. 0 = default, 100-500 recommended for speed.
    /// </summary>
    public int DeepgramEndpointing { get; set; } = 300;

    /// <summary>
    /// Redact sensitive information (pci, ssn, numbers, etc). Empty = disabled.
    /// </summary>
    public string DeepgramRedact { get; set; } = string.Empty;

    /// <summary>
    /// Opt out of Deepgram Model Improvement Program (privacy).
    /// </summary>
    public bool DeepgramMipOptOut { get; set; } = false;

    // ===== Custom Prompts =====
    
    /// <summary>
    /// Custom prompt for code dictation (Python). Empty = use default.
    /// </summary>
    public string CustomCodeDictationPrompt { get; set; } = string.Empty;
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

