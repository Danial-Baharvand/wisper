namespace WisperFlow.Models;

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
    public string PromptTemplate { get; init; } = "default";
    
    public string SizeFormatted => FormatSize(SizeBytes);
    
    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_000_000_000) return $"{bytes / 1_000_000_000.0:F1} GB";
        if (bytes >= 1_000_000) return $"{bytes / 1_000_000.0:F0} MB";
        if (bytes > 0) return $"{bytes / 1_000.0:F0} KB";
        return "Cloud";
    }
}

public enum ModelType { Whisper, LLM }
public enum ModelSource { OpenAI, Local }

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
    
    // Multilingual models
    public static readonly ModelInfo WhisperTiny = new()
    {
        Id = "whisper-tiny",
        Name = "Whisper Tiny",
        Description = "Fastest, multilingual (39M params)",
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
        Description = "Good balance, multilingual (74M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 142_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        FileName = "ggml-base.bin"
    };
    
    public static readonly ModelInfo WhisperSmall = new()
    {
        Id = "whisper-small",
        Name = "Whisper Small",
        Description = "Better accuracy, multilingual (244M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 466_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        FileName = "ggml-small.bin"
    };
    
    // English-only models (faster for English)
    public static readonly ModelInfo WhisperTinyEn = new()
    {
        Id = "whisper-tiny-en",
        Name = "Whisper Tiny (EN)",
        Description = "Fastest, English-only (39M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 75_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",
        FileName = "ggml-tiny.en.bin"
    };
    
    public static readonly ModelInfo WhisperBaseEn = new()
    {
        Id = "whisper-base-en",
        Name = "Whisper Base (EN)",
        Description = "Good balance, English-only (74M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 142_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin",
        FileName = "ggml-base.en.bin"
    };
    
    public static readonly ModelInfo WhisperSmallEn = new()
    {
        Id = "whisper-small-en",
        Name = "Whisper Small (EN)",
        Description = "Better accuracy, English-only (244M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 466_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin",
        FileName = "ggml-small.en.bin"
    };
    
    public static readonly ModelInfo WhisperMediumEn = new()
    {
        Id = "whisper-medium-en",
        Name = "Whisper Medium (EN)",
        Description = "High accuracy, English-only (769M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 1_500_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin",
        FileName = "ggml-medium.en.bin"
    };
    
    // Large/Turbo models
    public static readonly ModelInfo WhisperLargeV3Turbo = new()
    {
        Id = "whisper-large-v3-turbo",
        Name = "Whisper Large-v3 Turbo",
        Description = "8x faster than Large, near-same accuracy (809M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 1_600_000_000,
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin",
        FileName = "ggml-large-v3-turbo.bin"
    };
    
    public static readonly ModelInfo DistilWhisperLargeV3 = new()
    {
        Id = "distil-whisper-large-v3",
        Name = "Distil-Whisper Large-v3",
        Description = "6x faster, English-only, 1% WER of original",
        Type = ModelType.Whisper,
        Source = ModelSource.Local,
        SizeBytes = 1_500_000_000,
        DownloadUrl = "https://huggingface.co/distil-whisper/distil-large-v3.5-ggml/resolve/main/ggml-model.bin",
        FileName = "ggml-distil-large-v3.bin"
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
    
    // Working local models - verified URLs
    public static readonly ModelInfo TinyLlama = new()
    {
        Id = "tinyllama-1b",
        Name = "TinyLlama 1.1B",
        Description = "Small, CPU-friendly (1.1B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 670_000_000,
        DownloadUrl = "https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF/resolve/main/tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf",
        FileName = "tinyllama-1.1b-q4.gguf",
        PromptTemplate = "tinyllama"
    };
    
    public static readonly ModelInfo Gemma2B = new()
    {
        Id = "gemma-2b",
        Name = "Gemma 1 (2B)",
        Description = "Google's original Gemma, good quality (2B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 1_500_000_000,
        DownloadUrl = "https://huggingface.co/lmstudio-ai/gemma-2b-it-GGUF/resolve/main/gemma-2b-it-q4_k_m.gguf",
        FileName = "gemma-2b-q4.gguf",
        PromptTemplate = "gemma"
    };
    
    public static readonly ModelInfo Gemma2_2B = new()
    {
        Id = "gemma2-2b",
        Name = "Gemma 2 (2B)",
        Description = "Google's Gemma 2, improved quality (2B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 1_600_000_000,
        DownloadUrl = "https://huggingface.co/bartowski/gemma-2-2b-it-GGUF/resolve/main/gemma-2-2b-it-Q4_K_M.gguf",
        FileName = "gemma-2-2b-q4.gguf",
        PromptTemplate = "gemma2"
    };
    
    public static readonly ModelInfo Gemma2_9B = new()
    {
        Id = "gemma2-9b",
        Name = "Gemma 2 (9B)",
        Description = "High quality, needs 8GB+ RAM (9B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 5_500_000_000,
        DownloadUrl = "https://huggingface.co/bartowski/gemma-2-9b-it-GGUF/resolve/main/gemma-2-9b-it-Q4_K_M.gguf",
        FileName = "gemma-2-9b-q4.gguf",
        PromptTemplate = "gemma2"
    };
    
    // Note: Gemma 3 1B uses gemma3_text architecture which is not yet supported by LLamaSharp
    // Keeping Gemma 2 2B as the smallest fast option instead
    
    public static readonly ModelInfo Gemma3_4B = new()
    {
        Id = "gemma3-4b",
        Name = "Gemma 3 (4B)",
        Description = "Latest Gemma, excellent quality (4B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 2_600_000_000,
        DownloadUrl = "https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf",
        FileName = "gemma-3-4b-q4.gguf",
        PromptTemplate = "gemma3"
    };
    
    // Additional CPU models with verified URLs
    public static readonly ModelInfo Mistral7BInstruct = new()
    {
        Id = "mistral-7b",
        Name = "Mistral 7B Instruct",
        Description = "High quality, needs 8GB+ RAM (7B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 4_400_000_000,
        DownloadUrl = "https://huggingface.co/TheBloke/Mistral-7B-Instruct-v0.2-GGUF/resolve/main/mistral-7b-instruct-v0.2.Q4_K_M.gguf",
        FileName = "mistral-7b-instruct-q4.gguf",
        PromptTemplate = "mistral"
    };
    
    public static readonly ModelInfo OpenChat35 = new()
    {
        Id = "openchat-3.5",
        Name = "OpenChat 3.5",
        Description = "High quality, needs 8GB+ RAM (7B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 4_400_000_000,
        DownloadUrl = "https://huggingface.co/TheBloke/openchat-3.5-0106-GGUF/resolve/main/openchat-3.5-0106.Q4_K_M.gguf",
        FileName = "openchat-3.5-q4.gguf",
        PromptTemplate = "openchat"
    };
    
    public static IReadOnlyList<ModelInfo> WhisperModels { get; } = new[]
    {
        OpenAIWhisper,
        // English-only models (recommended for English)
        WhisperTinyEn, WhisperBaseEn, WhisperSmallEn, WhisperMediumEn,
        DistilWhisperLargeV3,
        // Faster large model
        WhisperLargeV3Turbo,
        // Multilingual models
        WhisperTiny, WhisperBase, WhisperSmall
    };
    
    public static IReadOnlyList<ModelInfo> LLMModels { get; } = new[]
    {
        OpenAIGpt4oMini, PolishDisabled, TinyLlama, Gemma2B, Gemma2_2B, Gemma2_9B, Gemma3_4B, Mistral7BInstruct, OpenChat35
    };
    
    public static ModelInfo? GetById(string id) =>
        WhisperModels.Concat(LLMModels).FirstOrDefault(m => m.Id == id);
}
