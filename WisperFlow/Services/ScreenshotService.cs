using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

/// <summary>
/// Service for capturing screenshots of the active window.
/// Used to provide visual context to AI queries.
/// </summary>
public class ScreenshotService
{
    private readonly ILogger<ScreenshotService> _logger;
    
    // Store the last captured screenshot
    private byte[]? _lastScreenshot;
    private DateTime _lastCaptureTime;

    public ScreenshotService(ILogger<ScreenshotService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the last captured screenshot, or null if none exists.
    /// </summary>
    public byte[]? LastScreenshot => _lastScreenshot;

    /// <summary>
    /// Gets the time of the last capture.
    /// </summary>
    public DateTime LastCaptureTime => _lastCaptureTime;

    /// <summary>
    /// Captures a screenshot of the currently active window.
    /// Should be called immediately when hotkey is pressed, before any UI appears.
    /// </summary>
    /// <returns>PNG image bytes, or null if capture failed.</returns>
    public byte[]? CaptureActiveWindow()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("No foreground window found");
                return null;
            }

            // Get window rectangle
            if (!GetWindowRect(hwnd, out RECT rect))
            {
                _logger.LogWarning("Failed to get window rect");
                return null;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
            {
                _logger.LogWarning("Invalid window dimensions: {Width}x{Height}", width, height);
                return null;
            }

            // Capture the window
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

            // Convert to PNG bytes
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            
            _lastScreenshot = ms.ToArray();
            _lastCaptureTime = DateTime.UtcNow;
            
            _logger.LogDebug("Captured screenshot: {Width}x{Height}, {Size} bytes", width, height, _lastScreenshot.Length);
            
            return _lastScreenshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture screenshot");
            return null;
        }
    }

    /// <summary>
    /// Clears the stored screenshot.
    /// </summary>
    public void ClearScreenshot()
    {
        _lastScreenshot = null;
    }

    /// <summary>
    /// Gets the last screenshot as a base64 string for API usage.
    /// </summary>
    public string? GetLastScreenshotAsBase64()
    {
        if (_lastScreenshot == null) return null;
        return Convert.ToBase64String(_lastScreenshot);
    }

    #region Win32 APIs

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    #endregion
}
