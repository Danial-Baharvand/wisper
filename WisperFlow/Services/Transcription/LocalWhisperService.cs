using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Whisper.net;
using WisperFlow.Models;

namespace WisperFlow.Services.Transcription;

/// <summary>
/// Transcription service using local Whisper.net model.
/// Automatically uses GPU (CUDA) when available, falls back to CPU.
/// </summary>
public class LocalWhisperService : ITranscriptionService
{
    private readonly ILogger _logger;
    private readonly ModelManager _modelManager;
    private readonly ModelInfo _model;
    private WhisperProcessor? _processor;
    private bool _isInitialized;

    public string ModelId => _model.Id;
    public bool IsReady => _isInitialized && _processor != null;

    public LocalWhisperService(ILogger logger, ModelManager modelManager, ModelInfo model)
    {
        _logger = logger;
        _modelManager = modelManager;
        _model = model;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        var modelPath = _modelManager.GetModelPath(_model);
        
        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Downloading Whisper model: {Model}", _model.Name);
            await _modelManager.DownloadModelAsync(_model, cancellationToken);
        }

        _logger.LogInformation("Loading Whisper model: {Model} from {Path}", _model.Name, modelPath);
        
        try
        {
            // Whisper.net automatically uses GPU (CUDA) if Whisper.net.Runtime.Cuda is available
            // and falls back to CPU otherwise
            var factory = WhisperFactory.FromPath(modelPath);
            
            _processor = factory.CreateBuilder()
                .WithLanguage("auto")
                .WithThreads(Environment.ProcessorCount)  // Use all CPU cores for maximum speed
                .Build();
            
            _isInitialized = true;
            var backend = DetectBackend();
            _logger.LogInformation("Whisper model loaded successfully - Backend: {Backend}", backend);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Whisper model");
            throw new InvalidOperationException($"Failed to load Whisper model: {ex.Message}", ex);
        }
    }

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (_processor == null)
            throw new InvalidOperationException("Whisper not initialized. Call InitializeAsync first.");

        _logger.LogInformation("Transcribing locally with {Model}", _model.Name);
        var startTime = DateTime.UtcNow;

        try
        {
            var result = new StringBuilder();
            
            await using var fileStream = File.OpenRead(audioFilePath);
            await foreach (var segment in _processor.ProcessAsync(fileStream, cancellationToken))
            {
                result.Append(segment.Text);
            }

            var text = result.ToString().Trim();
            var elapsed = DateTime.UtcNow - startTime;
            _logger.LogInformation("Local transcription complete: {Len} chars in {Time:F1}s", 
                text.Length, elapsed.TotalSeconds);
            
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local transcription failed");
            throw new InvalidOperationException($"Transcription failed: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _processor = null;
        _isInitialized = false;
    }
    
    /// <summary>
    /// Detects which Whisper runtime backend is being used.
    /// Priority: CUDA > OpenVINO > CPU
    /// </summary>
    private string DetectBackend()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            
            // Check for CUDA (NVIDIA GPU)
            if (assemblies.Any(a => a.GetName().Name?.Contains("Whisper.net.Runtime.Cuda") == true))
            {
                return "CUDA (NVIDIA GPU)";
            }
            
            // Check for OpenVINO (Intel acceleration)
            if (assemblies.Any(a => a.GetName().Name?.Contains("Whisper.net.Runtime.OpenVino") == true))
            {
                return "OpenVINO (Intel)";
            }
            
            // Check for CoreML (Apple)
            if (assemblies.Any(a => a.GetName().Name?.Contains("Whisper.net.Runtime.CoreML") == true))
            {
                return "CoreML (Apple)";
            }
            
            return "CPU";
        }
        catch
        {
            return "CPU";
        }
    }
}

