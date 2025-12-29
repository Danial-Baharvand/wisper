using System.Diagnostics;

namespace WisperFlow.Services.Transcription;

/// <summary>
/// Checks if faster-whisper Python environment is available.
/// </summary>
public static class FasterWhisperAvailability
{
    private static bool? _isAvailable;
    private static string? _pythonPath;
    private static string? _unavailableReason;

    /// <summary>
    /// Whether faster-whisper is available (Python 3.8-3.12 + faster-whisper package installed).
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_isAvailable == null)
                Check();
            return _isAvailable ?? false;
        }
    }

    /// <summary>
    /// The Python executable path if available.
    /// </summary>
    public static string? PythonPath => _pythonPath;

    /// <summary>
    /// Reason why faster-whisper is not available.
    /// </summary>
    public static string UnavailableReason => _unavailableReason ?? "Not checked";

    /// <summary>
    /// Force a re-check of availability.
    /// </summary>
    public static void Refresh()
    {
        _isAvailable = null;
        _pythonPath = null;
        _unavailableReason = null;
        Check();
    }

    private static void Check()
    {
        _pythonPath = FindCompatiblePython();
        
        if (_pythonPath == null)
        {
            _isAvailable = false;
            _unavailableReason = "Python 3.8-3.12 not found. Python 3.13+ is not supported.";
            return;
        }

        // Check if faster-whisper is installed
        if (!IsFasterWhisperInstalled(_pythonPath))
        {
            _isAvailable = false;
            _unavailableReason = $"faster-whisper not installed. Run: {GetPipCommand(_pythonPath)} install faster-whisper";
            return;
        }

        _isAvailable = true;
        _unavailableReason = null;
    }

    private static string? FindCompatiblePython()
    {
        // Try specific versions via py launcher (Windows)
        string[] pyVersions = { "-3.12", "-3.11", "-3.10", "-3.9" };
        foreach (var ver in pyVersions)
        {
            if (TryPython("py", ver, out var path))
                return path;
        }

        // Try generic python commands
        string[] pythonNames = { "python3.12", "python3.11", "python3.10", "python", "python3" };
        foreach (var name in pythonNames)
        {
            if (TryPythonDirect(name, out var path, out var version))
            {
                // Reject 3.13+
                if (!version.Contains("3.13") && !version.Contains("3.14") && !version.Contains("3.15"))
                    return path;
            }
        }

        return null;
    }

    private static bool TryPython(string launcher, string version, out string? path)
    {
        path = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = $"{version} --version",
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
                {
                    path = $"py {version}";
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool TryPythonDirect(string name, out string? path, out string version)
    {
        path = null;
        version = "";
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
                version = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (process.ExitCode == 0)
                {
                    path = name;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool IsFasterWhisperInstalled(string pythonPath)
    {
        try
        {
            string fileName;
            string arguments;
            
            if (pythonPath.StartsWith("py "))
            {
                fileName = "py";
                var ver = pythonPath.Substring(3);
                arguments = $"{ver} -c \"import faster_whisper\"";
            }
            else
            {
                fileName = pythonPath;
                arguments = "-c \"import faster_whisper\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(10000);
                return process.ExitCode == 0;
            }
        }
        catch { }
        return false;
    }

    private static string GetPipCommand(string pythonPath)
    {
        if (pythonPath.StartsWith("py "))
        {
            var ver = pythonPath.Substring(3);
            return $"py {ver} -m pip";
        }
        return $"{pythonPath} -m pip";
    }
}

