using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using WisperFlow.Services;
using WisperFlow.Services.Polish;
using WisperFlow.Services.Transcription;

namespace WisperFlow;

/// <summary>
/// Benchmarks transcription and polishing models for speed comparison.
/// Run with: dotnet run --project WisperFlow -- --benchmark
/// </summary>
public static class ModelBenchmark
{
    private static readonly string SampleText = @"Hello, this is a test of the speech to text system. 
I'm going to say a few sentences to see how well the transcription works. 
The quick brown fox jumps over the lazy dog. 
This is um, like, you know, a test with some filler words that should be removed.
Let's see how fast the polishing model can clean this up.";

    private static readonly string SampleAudioPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WisperFlow", "benchmark_audio.wav");

    public static async Task RunBenchmark(ILoggerFactory loggerFactory, ModelManager modelManager)
    {
        var logger = loggerFactory.CreateLogger("ModelBenchmark");
        
        logger.LogInformation("╔══════════════════════════════════════════════════════════════╗");
        logger.LogInformation("║              MODEL SPEED BENCHMARK                           ║");
        logger.LogInformation("╚══════════════════════════════════════════════════════════════╝");
        logger.LogInformation("");

        // Check for sample audio
        string? audioPath = FindOrCreateSampleAudio(logger);
        
        // Benchmark Transcription Models
        if (audioPath != null)
        {
            await BenchmarkTranscriptionModels(loggerFactory, modelManager, audioPath, logger);
        }
        else
        {
            logger.LogWarning("No sample audio found. Skipping transcription benchmarks.");
            logger.LogWarning("To benchmark transcription, place a WAV file at: {Path}", SampleAudioPath);
            logger.LogWarning("Or copy wisperflow_debug.wav from Desktop if available.");
        }

        logger.LogInformation("");
        
        // Benchmark Polish Models
        await BenchmarkPolishModels(loggerFactory, modelManager, logger);
        
        logger.LogInformation("");
        logger.LogInformation("╔══════════════════════════════════════════════════════════════╗");
        logger.LogInformation("║              BENCHMARK COMPLETE                              ║");
        logger.LogInformation("╚══════════════════════════════════════════════════════════════╝");
    }

    private static string? FindOrCreateSampleAudio(ILogger logger)
    {
        // Check for existing benchmark audio
        if (File.Exists(SampleAudioPath))
        {
            logger.LogInformation("Using benchmark audio: {Path}", SampleAudioPath);
            return SampleAudioPath;
        }

        // Check for debug audio on desktop
        var desktopAudio = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "wisperflow_debug.wav");
        
        if (File.Exists(desktopAudio))
        {
            logger.LogInformation("Using debug audio from desktop: {Path}", desktopAudio);
            return desktopAudio;
        }

        // Check temp folder for any wisperflow audio
        var tempFolder = Path.GetTempPath();
        var tempAudio = Directory.GetFiles(tempFolder, "wisperflow_*.wav").FirstOrDefault();
        if (tempAudio != null)
        {
            logger.LogInformation("Using temp audio: {Path}", tempAudio);
            return tempAudio;
        }

        return null;
    }

    private static async Task BenchmarkTranscriptionModels(
        ILoggerFactory loggerFactory, 
        ModelManager modelManager, 
        string audioPath,
        ILogger logger)
    {
        logger.LogInformation("┌──────────────────────────────────────────────────────────────┐");
        logger.LogInformation("│  TRANSCRIPTION MODEL BENCHMARKS                              │");
        logger.LogInformation("├──────────────────────────────────────────────────────────────┤");
        
        var audioInfo = new FileInfo(audioPath);
        logger.LogInformation("│  Audio file: {Size:F2} KB                                    │", audioInfo.Length / 1024.0);
        logger.LogInformation("└──────────────────────────────────────────────────────────────┘");
        logger.LogInformation("");

        var results = new List<BenchmarkResult>();

        foreach (var model in ModelCatalog.WhisperModels)
        {
            // Skip faster-whisper models (require Python)
            if (model.Id.StartsWith("faster-whisper"))
            {
                logger.LogInformation("  {Model,-35} SKIPPED (requires Python)", model.Name);
                continue;
            }

            // Skip OpenAI (requires API key and costs money)
            if (model.Source == ModelSource.OpenAI)
            {
                logger.LogInformation("  {Model,-35} SKIPPED (API model)", model.Name);
                continue;
            }

            // Check if model is installed
            if (!modelManager.IsModelInstalled(model))
            {
                logger.LogInformation("  {Model,-35} NOT INSTALLED", model.Name);
                continue;
            }

            try
            {
                var service = new LocalWhisperService(
                    loggerFactory.CreateLogger<LocalWhisperService>(),
                    modelManager,
                    model);

                // Warm-up / initialization
                var initSw = Stopwatch.StartNew();
                await service.InitializeAsync();
                initSw.Stop();

                // Benchmark transcription (3 runs)
                var times = new List<double>();
                string? transcript = null;
                
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    transcript = await service.TranscribeAsync(audioPath, "en");
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                var avgTime = times.Average();
                var result = new BenchmarkResult
                {
                    ModelName = model.Name,
                    ModelType = "Transcription",
                    InitTimeMs = initSw.Elapsed.TotalMilliseconds,
                    AvgTimeMs = avgTime,
                    MinTimeMs = times.Min(),
                    MaxTimeMs = times.Max(),
                    OutputLength = transcript?.Length ?? 0
                };
                results.Add(result);

                logger.LogInformation("  {Model,-35} Init: {Init,6:F0}ms | Avg: {Avg,6:F0}ms | Range: {Min:F0}-{Max:F0}ms",
                    model.Name, result.InitTimeMs, result.AvgTimeMs, result.MinTimeMs, result.MaxTimeMs);

                service.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning("  {Model,-35} ERROR: {Error}", model.Name, ex.Message);
            }
        }

        // Summary
        if (results.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("  ─── TRANSCRIPTION SUMMARY ───");
            var fastest = results.OrderBy(r => r.AvgTimeMs).First();
            var slowest = results.OrderByDescending(r => r.AvgTimeMs).First();
            logger.LogInformation("  Fastest: {Model} ({Time:F0}ms)", fastest.ModelName, fastest.AvgTimeMs);
            logger.LogInformation("  Slowest: {Model} ({Time:F0}ms)", slowest.ModelName, slowest.AvgTimeMs);
        }
    }

    private static async Task BenchmarkPolishModels(
        ILoggerFactory loggerFactory,
        ModelManager modelManager,
        ILogger logger)
    {
        logger.LogInformation("┌──────────────────────────────────────────────────────────────┐");
        logger.LogInformation("│  POLISH MODEL BENCHMARKS                                     │");
        logger.LogInformation("├──────────────────────────────────────────────────────────────┤");
        logger.LogInformation("│  Input: {Len} characters                                     │", SampleText.Length);
        logger.LogInformation("└──────────────────────────────────────────────────────────────┘");
        logger.LogInformation("");

        var results = new List<BenchmarkResult>();

        foreach (var model in ModelCatalog.LLMModels)
        {
            if (model.Id == "polish-disabled") continue;

            // Skip OpenAI models (require API key)
            if (model.Source == ModelSource.OpenAI)
            {
                logger.LogInformation("  {Model,-35} SKIPPED (API model)", model.Name);
                continue;
            }

            // Check if model is installed
            if (!modelManager.IsModelInstalled(model))
            {
                logger.LogInformation("  {Model,-35} NOT INSTALLED", model.Name);
                continue;
            }

            try
            {
                var service = new LocalLLMPolishService(
                    loggerFactory.CreateLogger<LocalLLMPolishService>(),
                    modelManager,
                    model);

                // Warm-up / initialization
                var initSw = Stopwatch.StartNew();
                await service.InitializeAsync();
                initSw.Stop();

                if (!service.IsReady)
                {
                    logger.LogWarning("  {Model,-35} FAILED TO LOAD", model.Name);
                    continue;
                }

                // Benchmark polishing (3 runs)
                var times = new List<double>();
                string? polished = null;

                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    polished = await service.PolishAsync(SampleText);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                var avgTime = times.Average();
                var result = new BenchmarkResult
                {
                    ModelName = model.Name,
                    ModelType = "Polish",
                    InitTimeMs = initSw.Elapsed.TotalMilliseconds,
                    AvgTimeMs = avgTime,
                    MinTimeMs = times.Min(),
                    MaxTimeMs = times.Max(),
                    OutputLength = polished?.Length ?? 0
                };
                results.Add(result);

                logger.LogInformation("  {Model,-35} Init: {Init,6:F0}ms | Avg: {Avg,6:F0}ms | Range: {Min:F0}-{Max:F0}ms",
                    model.Name, result.InitTimeMs, result.AvgTimeMs, result.MinTimeMs, result.MaxTimeMs);

                service.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning("  {Model,-35} ERROR: {Error}", model.Name, ex.Message);
            }
        }

        // Summary
        if (results.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("  ─── POLISH SUMMARY ───");
            var fastest = results.OrderBy(r => r.AvgTimeMs).First();
            var slowest = results.OrderByDescending(r => r.AvgTimeMs).First();
            logger.LogInformation("  Fastest: {Model} ({Time:F0}ms)", fastest.ModelName, fastest.AvgTimeMs);
            logger.LogInformation("  Slowest: {Model} ({Time:F0}ms)", slowest.ModelName, slowest.AvgTimeMs);
            
            // Speed comparison
            if (results.Count > 1)
            {
                logger.LogInformation("");
                logger.LogInformation("  ─── SPEED RANKING ───");
                int rank = 1;
                foreach (var r in results.OrderBy(x => x.AvgTimeMs))
                {
                    var speedup = slowest.AvgTimeMs / r.AvgTimeMs;
                    logger.LogInformation("  {Rank}. {Model,-30} {Time,6:F0}ms ({Speedup:F1}x vs slowest)",
                        rank++, r.ModelName, r.AvgTimeMs, speedup);
                }
            }
        }
    }

    private class BenchmarkResult
    {
        public string ModelName { get; set; } = "";
        public string ModelType { get; set; } = "";
        public double InitTimeMs { get; set; }
        public double AvgTimeMs { get; set; }
        public double MinTimeMs { get; set; }
        public double MaxTimeMs { get; set; }
        public int OutputLength { get; set; }
    }
}

