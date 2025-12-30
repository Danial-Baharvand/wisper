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
    
    // Inference parameters
    public float Temperature { get; init; } = 0.2f;
    public float RepeatPenalty { get; init; } = 1.08f;
    
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
public enum ModelSource { OpenAI, Local, FasterWhisper, Deepgram, Cerebras }

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
    
    // ===== DEEPGRAM MODELS (cloud API, requires Deepgram API key) =====
    // Ultra-fast cloud transcription with industry-leading accuracy
    
    public static readonly ModelInfo DeepgramNova3 = new()
    {
        Id = "deepgram-nova-3",
        Name = "🚀 Deepgram Nova-3",
        Description = "Fastest cloud, <300ms latency, best accuracy",
        Type = ModelType.Whisper,
        Source = ModelSource.Deepgram,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo DeepgramNova2 = new()
    {
        Id = "deepgram-nova-2",
        Name = "🚀 Deepgram Nova-2",
        Description = "Great speed/accuracy, 30% cheaper than OpenAI",
        Type = ModelType.Whisper,
        Source = ModelSource.Deepgram,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo DeepgramNova2Medical = new()
    {
        Id = "deepgram-nova-2-medical",
        Name = "🚀 Deepgram Medical",
        Description = "Optimized for healthcare terminology",
        Type = ModelType.Whisper,
        Source = ModelSource.Deepgram,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo DeepgramWhisperCloud = new()
    {
        Id = "deepgram-whisper-cloud",
        Name = "🚀 Deepgram Whisper",
        Description = "OpenAI Whisper hosted by Deepgram, faster",
        Type = ModelType.Whisper,
        Source = ModelSource.Deepgram,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo DeepgramBase = new()
    {
        Id = "deepgram-base",
        Name = "🚀 Deepgram Base",
        Description = "Budget option, fastest, good for simple tasks",
        Type = ModelType.Whisper,
        Source = ModelSource.Deepgram,
        SizeBytes = 0
    };
    
    // ===== FASTER-WHISPER MODELS (requires Python + faster-whisper) =====
    // These use CTranslate2 for ~4x faster inference with INT8 quantization
    
    public static readonly ModelInfo FasterWhisperTinyEn = new()
    {
        Id = "faster-whisper-tiny-en",
        Name = "⚡ Faster Tiny (EN)",
        Description = "4x faster, INT8, English-only (39M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.FasterWhisper,
        SizeBytes = 0  // Downloaded by Python
    };
    
    public static readonly ModelInfo FasterWhisperBaseEn = new()
    {
        Id = "faster-whisper-base-en",
        Name = "⚡ Faster Base (EN)",
        Description = "4x faster, INT8, English-only (74M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.FasterWhisper,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo FasterWhisperSmallEn = new()
    {
        Id = "faster-whisper-small-en",
        Name = "⚡ Faster Small (EN)",
        Description = "4x faster, INT8, English-only (244M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.FasterWhisper,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo FasterWhisperMediumEn = new()
    {
        Id = "faster-whisper-medium-en",
        Name = "⚡ Faster Medium (EN)",
        Description = "4x faster, INT8, English-only (769M params)",
        Type = ModelType.Whisper,
        Source = ModelSource.FasterWhisper,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo FasterWhisperLargeV3 = new()
    {
        Id = "faster-whisper-large-v3",
        Name = "⚡ Faster Large-v3",
        Description = "4x faster, INT8, multilingual (1.55B params)",
        Type = ModelType.Whisper,
        Source = ModelSource.FasterWhisper,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo FasterWhisperDistilLargeV3 = new()
    {
        Id = "faster-whisper-distil-large-v3",
        Name = "⚡ Faster Distil-Large-v3",
        Description = "Fastest large model, INT8, English-only",
        Type = ModelType.Whisper,
        Source = ModelSource.FasterWhisper,
        SizeBytes = 0
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
    
    public static readonly ModelInfo OpenAIGpt5Mini = new()
    {
        Id = "openai-gpt5-mini",
        Name = "GPT-5 Mini (API)",
        Description = "Latest GPT-5, balanced speed & quality",
        Type = ModelType.LLM,
        Source = ModelSource.OpenAI,
        SizeBytes = 0
    };
    
    // ===== CEREBRAS MODELS (Cloud API - Fastest inference) =====
    
    public static readonly ModelInfo CerebrasLlama33_70B = new()
    {
        Id = "cerebras-llama-3.3-70b",
        Name = "⚡ Cerebras Llama 3.3 70B",
        Description = "Flagship model, 2200+ tokens/sec",
        Type = ModelType.LLM,
        Source = ModelSource.Cerebras,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo CerebrasLlama31_8B = new()
    {
        Id = "cerebras-llama3.1-8b",
        Name = "⚡ Cerebras Llama 3.1 8B",
        Description = "Fast & efficient 8B model",
        Type = ModelType.LLM,
        Source = ModelSource.Cerebras,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo CerebrasGptOss120B = new()
    {
        Id = "cerebras-gpt-oss-120b",
        Name = "⚡ Cerebras GPT-OSS 120B",
        Description = "Open-source 120B model, very capable",
        Type = ModelType.LLM,
        Source = ModelSource.Cerebras,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo CerebrasQwen3_32B = new()
    {
        Id = "cerebras-qwen-3-32b",
        Name = "⚡ Cerebras Qwen 3 32B",
        Description = "Qwen 3 32B, great reasoning",
        Type = ModelType.LLM,
        Source = ModelSource.Cerebras,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo CerebrasQwen3_235B = new()
    {
        Id = "cerebras-qwen-3-235b-a22b",
        Name = "⚡ Cerebras Qwen 3 235B",
        Description = "Largest Qwen, MoE architecture",
        Type = ModelType.LLM,
        Source = ModelSource.Cerebras,
        SizeBytes = 0
    };
    
    public static readonly ModelInfo CerebrasZaiGlm = new()
    {
        Id = "cerebras-zai-glm-4.6",
        Name = "⚡ Cerebras ZAI-GLM 4.6",
        Description = "ZAI GLM 4.6 model",
        Type = ModelType.LLM,
        Source = ModelSource.Cerebras,
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
    
    // ===== NEW 2024/2025 MODELS =====
    
    public static readonly ModelInfo Qwen25_3B = new()
    {
        Id = "qwen2.5-3b",
        Name = "Qwen 2.5 (3B) ⭐",
        Description = "Best overall quality (3B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 2_100_000_000,
        DownloadUrl = "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
        FileName = "qwen2.5-3b-instruct-q4.gguf",
        PromptTemplate = "qwen2",
        Temperature = 0.2f,
        RepeatPenalty = 1.08f
    };
    
    public static readonly ModelInfo Qwen3_1B = new()
    {
        Id = "qwen3-1.7b",
        Name = "Qwen 3 (1.7B) ⭐",
        Description = "Best speed/quality balance (1.7B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 1_300_000_000,
        DownloadUrl = "https://huggingface.co/lm-kit/qwen-3-1.7b-instruct-gguf/resolve/main/Qwen3-1.7B-Q4_K_M.gguf",
        FileName = "qwen3-1.7b-instruct-q4.gguf",
        PromptTemplate = "qwen3",
        Temperature = 0.2f,
        RepeatPenalty = 1.2f  // Higher penalty for Qwen3 quantized
    };
    
    public static readonly ModelInfo Gemma2_2B = new()
    {
        Id = "gemma2-2b",
        Name = "Gemma 2 (2B)",
        Description = "Good writer, Google (2B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 1_600_000_000,
        DownloadUrl = "https://huggingface.co/unsloth/gemma-2-it-GGUF/resolve/main/gemma-2-2b-it.q4_k_m.gguf",
        FileName = "gemma-2-2b-it-q4.gguf",
        PromptTemplate = "gemma2",
        Temperature = 0.2f,
        RepeatPenalty = 1.15f  // Gemma needs higher repeat penalty
    };
    
    public static readonly ModelInfo Llama32_3B = new()
    {
        Id = "llama3.2-3b",
        Name = "Llama 3.2 (3B)",
        Description = "Strong generalist (3B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 2_000_000_000,
        DownloadUrl = "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf",
        FileName = "llama-3.2-3b-instruct-q4.gguf",
        PromptTemplate = "llama3",
        Temperature = 0.2f,
        RepeatPenalty = 1.08f
    };
    
    public static readonly ModelInfo SmolLM2_1B = new()
    {
        Id = "smollm2-1.7b",
        Name = "SmolLM2 (1.7B)",
        Description = "Tiny & fast (1.7B params)",
        Type = ModelType.LLM,
        Source = ModelSource.Local,
        SizeBytes = 1_100_000_000,
        DownloadUrl = "https://huggingface.co/HuggingFaceTB/SmolLM2-1.7B-Instruct-GGUF/resolve/main/smollm2-1.7b-instruct-q4_k_m.gguf",
        FileName = "smollm2-1.7b-instruct-q4.gguf",
        PromptTemplate = "smollm",
        Temperature = 0.2f,
        RepeatPenalty = 1.08f
    };
    
    public static IReadOnlyList<ModelInfo> WhisperModels { get; } = new[]
    {
        // Cloud APIs (fastest, requires API key)
        OpenAIWhisper,
        DeepgramNova3, DeepgramNova2, DeepgramWhisperCloud, DeepgramNova2Medical, DeepgramBase,
        // Faster-Whisper models (fast local, requires Python)
        FasterWhisperBaseEn, FasterWhisperSmallEn, FasterWhisperMediumEn,
        FasterWhisperDistilLargeV3, FasterWhisperLargeV3, FasterWhisperTinyEn,
        // Standard Whisper.net models (local)
        WhisperTinyEn, WhisperBaseEn, WhisperSmallEn, WhisperMediumEn,
        DistilWhisperLargeV3, WhisperLargeV3Turbo,
        // Multilingual models
        WhisperTiny, WhisperBase, WhisperSmall
    };
    
    public static IReadOnlyList<ModelInfo> LLMModels { get; } = new[]
    {
        // Disabled option (first in list)
        PolishDisabled,
        // Cloud models (API)
        OpenAIGpt5Mini, OpenAIGpt4oMini, 
        // Cerebras Cloud (fastest inference)
        CerebrasLlama33_70B, CerebrasLlama31_8B, CerebrasGptOss120B,
        CerebrasQwen3_32B, CerebrasQwen3_235B, CerebrasZaiGlm,
        // Best local models (2024/2025)
        Qwen25_3B, Qwen3_1B, Gemma2_2B, Llama32_3B, SmolLM2_1B,
        // Other local models
        Gemma3_4B, Mistral7BInstruct, OpenChat35
    };
    
    /// <summary>
    /// Models suitable for code dictation (3B+ params or API models).
    /// Excludes smaller models that struggle with code generation quality.
    /// </summary>
    public static IReadOnlyList<ModelInfo> CodeDictationModels { get; } = new[]
    {
        // Cloud models (API) - always included
        OpenAIGpt5Mini, OpenAIGpt4oMini,
        // Cerebras Cloud (fastest inference)
        CerebrasLlama33_70B, CerebrasLlama31_8B, CerebrasGptOss120B,
        CerebrasQwen3_32B, CerebrasQwen3_235B, CerebrasZaiGlm,
        // 3B+ local models only (recommended for code)
        Qwen25_3B,      // 3B - best overall quality
        Llama32_3B,     // 3B - strong generalist
        Gemma3_4B,      // 4B - excellent quality
        Mistral7BInstruct, // 7B - high quality
        OpenChat35      // 7B - high quality
    };
    
    /// <summary>
    /// Available programming languages for code dictation.
    /// </summary>
    public static IReadOnlyList<(string Code, string Name)> CodeDictationLanguages { get; } = new[]
    {
        ("python", "Python")
        // Future: ("javascript", "JavaScript"), ("csharp", "C#"), etc.
    };
    
    public static ModelInfo? GetById(string id) =>
        WhisperModels.Concat(LLMModels).FirstOrDefault(m => m.Id == id);
}
