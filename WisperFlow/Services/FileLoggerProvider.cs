using System.IO;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        try
        {
            if (File.Exists(filePath))
            {
                var backupPath = filePath + ".old";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(filePath, backupPath);
            }
        }
        catch { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_filePath, categoryName, _lock);
    public void Dispose() { }
}

internal class FileLogger : ILogger
{
    private readonly string _filePath;
    private readonly string _categoryName;
    private readonly object _lock;

    public FileLogger(string filePath, string categoryName, object lockObj)
    {
        _filePath = filePath;
        _categoryName = categoryName;
        _lock = lockObj;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        var logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel,-11}] {_categoryName}: {message}";
        if (exception != null) logLine += Environment.NewLine + exception.ToString();
        lock (_lock)
        {
            try { File.AppendAllText(_filePath, logLine + Environment.NewLine); }
            catch { }
        }
    }
}