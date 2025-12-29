using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services.Transcription;

/// <summary>
/// Transcription service using faster-whisper via Python sidecar process.
/// Provides up to 4x faster transcription compared to standard Whisper.
/// </summary>
public class FasterWhisperService : ITranscriptionService
{
    private readonly ILogger _logger;
    private readonly ModelInfo _model;
    private Process? _pythonProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _isInitialized;
    private readonly object _lock = new();
    private readonly string _scriptPath;

    public string ModelId => _model.Id;
    public bool IsReady => _isInitialized && _pythonProcess is { HasExited: false };

    public FasterWhisperService(ILogger logger, ModelInfo model)
    {
        _logger = logger;
        _model = model;
        
        // Script is in the Scripts folder relative to the executable
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        _scriptPath = Path.Combine(exeDir, "Scripts", "faster_whisper_server.py");
        
        // Also check the source directory for development
        if (!File.Exists(_scriptPath))
        {
            var devPath = Path.Combine(exeDir, "..", "..", "..", "Scripts", "faster_whisper_server.py");
            if (File.Exists(devPath))
                _scriptPath = Path.GetFullPath(devPath);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        lock (_lock)
        {
            if (_isInitialized) return;

            _logger.LogInformation("Starting faster-whisper server for model: {Model}", _model.Name);

            if (!File.Exists(_scriptPath))
            {
                throw new InvalidOperationException(
                    $"faster-whisper script not found at: {_scriptPath}. " +
                    "Please ensure Python and faster-whisper are installed.");
            }

            try
            {
                // Find Python executable
                var pythonPath = FindPythonExecutable();
                if (pythonPath == null)
                {
                    throw new InvalidOperationException(
                        "Compatible Python not found. faster-whisper requires Python 3.8-3.12. " +
                        "Python 3.13+ is not supported. Please install Python 3.11 or 3.12.");
                }

                _logger.LogDebug("Using Python: {Python}", pythonPath);

                // Handle "py -3.12" format (py launcher with version)
                string fileName;
                string arguments;
                if (pythonPath.StartsWith("py "))
                {
                    fileName = "py";
                    var version = pythonPath.Substring(3); // e.g., "-3.12"
                    arguments = $"{version} \"{_scriptPath}\"";
                }
                else
                {
                    fileName = pythonPath;
                    arguments = $"\"{_scriptPath}\"";
                }

                // Start the Python process
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_scriptPath)
                };

                _pythonProcess = Process.Start(psi);
                if (_pythonProcess == null)
                {
                    throw new InvalidOperationException("Failed to start Python process");
                }

                _stdin = _pythonProcess.StandardInput;
                _stdout = _pythonProcess.StandardOutput;

                // Log stderr in background
                _ = Task.Run(() => LogStderr(_pythonProcess.StandardError));

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start faster-whisper server");
                Cleanup();
                throw;
            }
        }

        // Wait for ready signal with timeout
        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readyTimeout.Token);
        
        try
        {
            var readyResponse = await ReadResponseAsync(combined.Token);
            
            // Check if we got an error response (e.g., faster-whisper not installed)
            if (readyResponse.HasValue && readyResponse.Value.TryGetProperty("error", out var errorProp))
            {
                var errorMsg = errorProp.GetString() ?? "Unknown Python error";
                _logger.LogError("faster-whisper Python error: {Error}", errorMsg);
                Cleanup();
                throw new InvalidOperationException($"faster-whisper error: {errorMsg}");
            }
            
            if (!readyResponse.HasValue || 
                !readyResponse.Value.TryGetProperty("ready", out var ready) || 
                !ready.GetBoolean())
            {
                // Try to read stderr for more info
                var stderrInfo = "";
                if (_pythonProcess != null && _pythonProcess.HasExited)
                {
                    stderrInfo = $" (Python exited with code {_pythonProcess.ExitCode})";
                }
                Cleanup();
                throw new InvalidOperationException(
                    $"faster-whisper server did not send ready signal{stderrInfo}. " +
                    "Make sure Python and faster-whisper are installed: pip install faster-whisper");
            }
        }
        catch (OperationCanceledException) when (readyTimeout.IsCancellationRequested)
        {
            Cleanup();
            throw new InvalidOperationException(
                "faster-whisper server timed out. Make sure faster-whisper is installed: pip install faster-whisper");
        }

        _logger.LogInformation("faster-whisper server ready");

        // Load the model
        var modelSize = GetModelSize();
        var loadResult = await SendCommandAsync(new
        {
            command = "load",
            model_size = modelSize,
            device = "auto",
            compute_type = "auto"
        }, cancellationToken);

        if (!loadResult.HasValue || 
            !loadResult.Value.TryGetProperty("success", out var success) || 
            !success.GetBoolean())
        {
            var error = "Unknown error";
            if (loadResult.HasValue && loadResult.Value.TryGetProperty("error", out var errProp))
                error = errProp.GetString() ?? error;
            throw new InvalidOperationException($"Failed to load model: {error}");
        }

        var device = loadResult.Value.TryGetProperty("device", out var deviceProp) ? deviceProp.GetString() : "unknown";
        var computeType = loadResult.Value.TryGetProperty("compute_type", out var ctProp) ? ctProp.GetString() : "unknown";
        _logger.LogInformation("faster-whisper model loaded: {Model} on {Device} ({ComputeType})", 
            modelSize, device, computeType);
    }

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsReady)
            throw new InvalidOperationException("faster-whisper not initialized. Call InitializeAsync first.");

        _logger.LogInformation("Transcribing with faster-whisper: {Model}", _model.Name);
        var startTime = DateTime.UtcNow;

        var result = await SendCommandAsync(new
        {
            command = "transcribe",
            audio_path = audioFilePath,
            language = language
        }, cancellationToken);

        if (!result.HasValue)
        {
            throw new InvalidOperationException("No response from faster-whisper server");
        }

        if (!result.Value.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var error = result.Value.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
            throw new InvalidOperationException($"Transcription failed: {error}");
        }

        var text = result.Value.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
        var duration = result.Value.TryGetProperty("duration", out var durProp) ? durProp.GetDouble() : 0;

        _logger.LogInformation("faster-whisper transcription complete: {Len} chars in {Time:F2}s", 
            text.Length, duration);

        return text;
    }

    private async Task<JsonElement?> SendCommandAsync(object command, CancellationToken cancellationToken)
    {
        if (_stdin == null || _stdout == null)
            return null;

        var json = JsonSerializer.Serialize(command);
        
        lock (_lock)
        {
            _stdin.WriteLine(json);
            _stdin.Flush();
        }

        return await ReadResponseAsync(cancellationToken);
    }

    private async Task<JsonElement?> ReadResponseAsync(CancellationToken cancellationToken)
    {
        if (_stdout == null) return null;

        try
        {
            var line = await _stdout.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line)) return null;

            return JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read response from faster-whisper");
            return null;
        }
    }

    private async void LogStderr(StreamReader stderr)
    {
        try
        {
            while (!stderr.EndOfStream)
            {
                var line = await stderr.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    // Log errors at Warning level so they're visible
                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("traceback", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[faster-whisper stderr] {Line}", line);
                    }
                    else
                    {
                        _logger.LogDebug("[faster-whisper stderr] {Line}", line);
                    }
                }
            }
        }
        catch
        {
            // Process ended
        }
    }

    private string GetModelSize()
    {
        // Map model ID to faster-whisper model size
        return _model.Id switch
        {
            "faster-whisper-tiny" => "tiny",
            "faster-whisper-tiny-en" => "tiny.en",
            "faster-whisper-base" => "base",
            "faster-whisper-base-en" => "base.en",
            "faster-whisper-small" => "small",
            "faster-whisper-small-en" => "small.en",
            "faster-whisper-medium" => "medium",
            "faster-whisper-medium-en" => "medium.en",
            "faster-whisper-large-v3" => "large-v3",
            "faster-whisper-large-v3-turbo" => "large-v3-turbo",
            "faster-whisper-distil-large-v3" => "distil-large-v3",
            _ => "base"
        };
    }

    private static string? FindPythonExecutable()
    {
        // Try to find a compatible Python version (3.8-3.12)
        // faster-whisper doesn't work with Python 3.13+ yet
        
        // First, try specific versions via py launcher (Windows)
        string[] pyVersions = { "-3.12", "-3.11", "-3.10", "-3.9" };
        foreach (var ver in pyVersions)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "py",
                    Arguments = $"{ver} --version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                        return $"py {ver}";
                }
            }
            catch
            {
                // Try next
            }
        }
        
        // Fall back to generic python commands
        string[] pythonNames = { "python3.12", "python3.11", "python3.10", "python", "python3", "py" };
        
        foreach (var name in pythonNames)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                    {
                        // Check version - reject 3.13+
                        if (output.Contains("3.13") || output.Contains("3.14") || output.Contains("3.15"))
                        {
                            continue; // Skip incompatible versions
                        }
                        return name;
                    }
                }
            }
            catch
            {
                // Try next
            }
        }

        return null;
    }

    private void Cleanup()
    {
        try
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            
            if (_pythonProcess is { HasExited: false })
            {
                _pythonProcess.Kill();
                _pythonProcess.WaitForExit(1000);
            }
            _pythonProcess?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
            _stdin = null;
            _stdout = null;
            _pythonProcess = null;
            _isInitialized = false;
        }
    }

    public void Dispose()
    {
        // Send quit command gracefully
        if (IsReady && _stdin != null)
        {
            try
            {
                _stdin.WriteLine("{\"command\":\"quit\"}");
                _stdin.Flush();
                _pythonProcess?.WaitForExit(2000);
            }
            catch
            {
                // Ignore
            }
        }

        Cleanup();
    }
}

