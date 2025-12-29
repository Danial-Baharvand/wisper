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
        Name = "Gemma 2B",
        Description = "Google's model, good quality (2B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 1_500_000_000,
        DownloadUrl = "https://huggingface.co/lmstudio-ai/gemma-2b-it-GGUF/resolve/main/gemma-2b-it-q4_k_m.gguf",
        FileName = "gemma-2b-q4.gguf",
        PromptTemplate = "gemma"
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
        OpenAIWhisper, WhisperTiny, WhisperBase, WhisperSmall
    };
    
    public static IReadOnlyList<ModelInfo> LLMModels { get; } = new[]
    {
        OpenAIGpt4oMini, PolishDisabled, TinyLlama, Gemma2B, Mistral7BInstruct, OpenChat35
    };
    
    public static ModelInfo? GetById(string id) =>
        WhisperModels.Concat(LLMModels).FirstOrDefault(m => m.Id == id);
}
