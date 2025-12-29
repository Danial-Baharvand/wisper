namespace WisperFlow.Models;

/// <summary>
/// Represents information about an AI model (Whisper or LLM).
/// </summary>
public class ModelInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ModelType Type { get; init; }
    public ModelSource Source { get; init; }
    public long SizeBytes { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    
    public string SizeFormatted => FormatSize(SizeBytes);
    
    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_000_000_000) return $"{bytes / 1_000_000_000.0:F1} GB";
        if (bytes >= 1_000_000) return $"{bytes / 1_000_000.0:F0} MB";
        if (bytes > 0) return $"{bytes / 1_000.0:F0} KB";
        return "Cloud";
    }
}

public enum ModelType
{
    Whisper,    // Speech-to-text
    LLM         // Text polish
}

public enum ModelSource
{
    OpenAI,     // Cloud API
    Local       // Downloaded model
}

/// <summary>
/// Tracks model download progress.
/// </summary>
public class DownloadProgress
{
    public string ModelId { get; set; } = "";
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public bool IsComplete { get; set; }
    public bool IsCancelled { get; set; }
    public string? Error { get; set; }
    
    public double ProgressPercent => TotalBytes > 0 ? (BytesDownloaded * 100.0 / TotalBytes) : 0;
}

/// <summary>
/// Static catalog of available models.
/// </summary>
public static class ModelCatalog
{
    // ===== WHISPER MODELS =====
    
    public static readonly ModelInfo OpenAIWhisper = new()
    {
        Id = "openai-whisper",
        Name = "OpenAI Whisper API",
        Description = "Cloud-based, highest quality, requires API key",
        Type = ModelType.Whisper,
        Source = ModelSource.OpenAI,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo WhisperTiny = new()
    {
        Id = "whisper-tiny",
        Name = "Whisper Tiny",
        Description = "Fastest, lower accuracy (39M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 75_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
        FileName = "ggml-tiny.bin"
    };
    
    public static readonly ModelInfo WhisperBase = new()
    {
        Id = "whisper-base",
        Name = "Whisper Base",
        Description = "Good balance of speed and accuracy (74M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 150_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        FileName = "ggml-base.bin"
    };
    
    public static readonly ModelInfo WhisperSmall = new()
    {
        Id = "whisper-small",
        Name = "Whisper Small",
        Description = "Better accuracy, slower (244M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 500_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        FileName = "ggml-small.bin"
    };
    
    // ===== LLM MODELS =====
    
    public static readonly ModelInfo OpenAIGpt4oMini = new()
    {
        Id = "openai-gpt4o-mini",
        Name = "GPT-4o-mini (API)",
        Description = "Cloud-based, fast and high quality",
        Type = ModelType.LLM,
        Source = ModelSource.OpenAI,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo PolishDisabled = new()
    {
        Id = "polish-disabled",
        Name = "Disabled",
        Description = "Use raw transcription without polish",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 0
    };
    
    // Small local LLM options
    public static readonly ModelInfo SmolLM135M = new()
    {
        Id = "smollm-135m",
        Name = "SmolLM 135M",
        Description = "Very small & fast, basic cleanup (135M params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 100_000_000,
        DownloadUrl = "https://huggingface.co/TheBloke/smollm-135M-instruct-v0.2-GGUF/resolve/main/smollm-135m-instruct-v0.2.Q4_K_M.gguf",
        FileName = "smollm-135m-q4.gguf"
    };
    
    public static readonly ModelInfo TinyLlama = new()
    {
        Id = "tinyllama-1b",
        Name = "TinyLlama 1.1B",
        Description = "Small but capable, good quality (1.1B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 670_000_000,
        DownloadUrl = "https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF/resolve/main/tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf",
        FileName = "tinyllama-1.1b-q4.gguf"
    };
    
    public static IReadOnlyList<ModelInfo> WhisperModels { get; } = new[]
    {
        OpenAIWhisper, WhisperTiny, WhisperBase, WhisperSmall
    };
    
    public static IReadOnlyList<ModelInfo> LLMModels { get; } = new[]
    {
        OpenAIGpt4oMini, PolishDisabled, SmolLM135M, TinyLlama
    };
    
    public static ModelInfo? GetById(string id) =>
        WhisperModels.Concat(LLMModels).FirstOrDefault(m => m.Id == id);
}

