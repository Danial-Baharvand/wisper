using System.IO;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace WisperFlow.Services;

public class AudioRecorder : IDisposable
{
    private readonly ILogger<AudioRecorder> _logger;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private string? _tempFilePath;
    private bool _isRecording;
    private DateTime _recordingStartTime;
    private int _maxDurationSeconds = 120;
    private System.Timers.Timer? _maxDurationTimer;
    private bool _disposed;
    private int _selectedDeviceNumber = -1;
    private long _totalBytesRecorded; // DEBUG

    private readonly WaveFormat _recordingFormat = new(16000, 16, 1);

    public event EventHandler? RecordingStarted;
    public event EventHandler<TimeSpan>? RecordingProgress;
    public event EventHandler? MaxDurationReached;
    /// <summary>
    /// Event fired when audio data is available during recording (for streaming transcription).
    /// </summary>
    public event EventHandler<byte[]>? AudioDataAvailable;
    
    /// <summary>
    /// Event fired with normalized audio level (0.0 to 1.0) for visualization.
    /// </summary>
    public event EventHandler<float>? AudioLevelChanged;

    public bool IsRecording => _isRecording;
    public TimeSpan RecordingDuration => _isRecording ? DateTime.Now - _recordingStartTime : TimeSpan.Zero;

    public AudioRecorder(ILogger<AudioRecorder> logger) { _logger = logger; }

    public void SetMaxDuration(int seconds) { _maxDurationSeconds = seconds; }

    public void SetDevice(int deviceNumber)
    {
        _selectedDeviceNumber = deviceNumber;
        // Log device name for debugging
        try
        {
            var deviceName = deviceNumber >= 0 && deviceNumber < WaveInEvent.DeviceCount 
                ? WaveInEvent.GetCapabilities(deviceNumber).ProductName 
                : "Default";
            _logger.LogInformation("Audio device set to: {DeviceNumber} ({DeviceName})", deviceNumber, deviceName);
        }
        catch
        {
            _logger.LogInformation("Audio device set to: {DeviceNumber}", deviceNumber);
        }
    }

    public List<(int DeviceNumber, string Name)> GetAvailableDevices()
    {
        var devices = new List<(int, string)>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var capabilities = WaveInEvent.GetCapabilities(i);
            devices.Add((i, capabilities.ProductName));
            _logger.LogDebug("Found audio device {Index}: {Name}", i, capabilities.ProductName);
        }
        return devices;
    }

    public void StartRecording()
    {
        if (_isRecording) { _logger.LogWarning("Already recording"); return; }

        try
        {
            _tempFilePath = Path.Combine(Path.GetTempPath(), $"wisperflow_{Guid.NewGuid():N}.wav");
            _logger.LogDebug("Recording to temp file: {TempFile}", _tempFilePath);

            int deviceToUse = _selectedDeviceNumber >= 0 ? _selectedDeviceNumber : 0;
            var deviceName = deviceToUse < WaveInEvent.DeviceCount 
                ? WaveInEvent.GetCapabilities(deviceToUse).ProductName 
                : "Unknown";
            _logger.LogInformation("Using audio device {DeviceNumber}: {DeviceName}", deviceToUse, deviceName);

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceToUse,
                WaveFormat = _recordingFormat,
                BufferMilliseconds = 50
            };

            _waveWriter = new WaveFileWriter(_tempFilePath, _recordingFormat);
            _totalBytesRecorded = 0; // DEBUG
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _maxDurationTimer = new System.Timers.Timer(_maxDurationSeconds * 1000);
            _maxDurationTimer.Elapsed += OnMaxDurationElapsed;
            _maxDurationTimer.AutoReset = false;
            _maxDurationTimer.Start();

            _waveIn.StartRecording();
            _isRecording = true;
            _recordingStartTime = DateTime.Now;

            _logger.LogInformation("Recording started");
            RecordingStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            CleanupRecording();
            throw;
        }
    }

    public string? StopRecording()
    {
        if (!_isRecording) { _logger.LogWarning("Not recording"); return null; }

        try
        {
            // Set flag FIRST to signal OnDataAvailable to stop writing
            _isRecording = false;
            
            _maxDurationTimer?.Stop();
            _maxDurationTimer?.Dispose();
            _maxDurationTimer = null;

            _waveIn?.StopRecording();
            
            // Small delay to let any in-flight OnDataAvailable callbacks complete
            Thread.Sleep(10);
            
            // CRITICAL: Dispose writer BEFORE returning path to release file lock
            _waveWriter?.Dispose();
            _waveWriter = null;
            
            _waveIn?.Dispose();
            _waveIn = null;
            
            var duration = DateTime.Now - _recordingStartTime;
            _logger.LogInformation("Recording stopped, duration: {Duration:F1}s, bytes recorded: {Bytes}", 
                duration.TotalSeconds, _totalBytesRecorded);

            return _tempFilePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping recording");
            CleanupRecording();
            return null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Capture to local variable for thread-safety (StopRecording can null these)
        var writer = _waveWriter;
        
        // Check both _isRecording and writer to avoid race conditions
        if (!_isRecording || writer == null || e.BytesRecorded <= 0)
            return;
        
        try
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            _totalBytesRecorded += e.BytesRecorded; // DEBUG
            
            // Fire event for streaming transcription
            if (AudioDataAvailable != null)
            {
                var audioChunk = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, audioChunk, e.BytesRecorded);
                AudioDataAvailable.Invoke(this, audioChunk);
            }
            
            // Calculate and fire audio level for visualization
            if (AudioLevelChanged != null)
            {
                float level = CalculateAudioLevel(e.Buffer, e.BytesRecorded);
                AudioLevelChanged.Invoke(this, level);
            }
            
            var duration = DateTime.Now - _recordingStartTime;
            if (duration.TotalMilliseconds % 500 < 50)
                RecordingProgress?.Invoke(this, duration);
        }
        catch (ObjectDisposedException)
        {
            // Writer was disposed between our check and write - this is expected during shutdown
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            _logger.LogError(e.Exception, "Recording stopped due to error");
    }
    
    /// <summary>
    /// Calculate normalized audio level (0.0 to 1.0) from 16-bit PCM samples.
    /// Uses RMS (root mean square) for more accurate level representation.
    /// </summary>
    private static float CalculateAudioLevel(byte[] buffer, int bytesRecorded)
    {
        // 16-bit samples = 2 bytes per sample
        int sampleCount = bytesRecorded / 2;
        if (sampleCount == 0) return 0f;
        
        double sumOfSquares = 0;
        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            // Convert to 16-bit signed sample
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            
            // Normalize to -1.0 to 1.0
            double normalizedSample = sample / 32768.0;
            sumOfSquares += normalizedSample * normalizedSample;
        }
        
        // RMS level
        double rms = Math.Sqrt(sumOfSquares / sampleCount);
        
        // Convert to 0-1 range with some scaling for better visual feedback
        // Typical speech is around 0.1-0.3 RMS
        float level = (float)(rms * 3.0);
        return Math.Clamp(level, 0f, 1f);
    }

    private void OnMaxDurationElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        _logger.LogWarning("Maximum recording duration reached ({MaxDuration}s)", _maxDurationSeconds);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            MaxDurationReached?.Invoke(this, EventArgs.Empty);
            StopRecording();
        });
    }

    private void CleanupRecording()
    {
        try
        {
            _waveWriter?.Dispose(); _waveWriter = null;
            _waveIn?.Dispose(); _waveIn = null;
            _maxDurationTimer?.Dispose(); _maxDurationTimer = null;
            _isRecording = false;
            if (_tempFilePath != null && File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
                _logger.LogDebug("Deleted temp recording file");
            }
            _tempFilePath = null;
        }
        catch (Exception ex) { _logger.LogError(ex, "Error during cleanup"); }
    }

    public void DeleteTempFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                // DEBUG: Copy to desktop instead of deleting so user can check the audio
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var debugFile = Path.Combine(desktopPath, "wisperflow_debug.wav");
                File.Copy(filePath, debugFile, overwrite: true);
                _logger.LogWarning("DEBUG: Audio saved to {DebugFile} for inspection", debugFile);
                
                File.Delete(filePath);
                _logger.LogDebug("Deleted temp file: {FilePath}", filePath);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temp file: {FilePath}", filePath); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupRecording();
        GC.SuppressFinalize(this);
    }
}