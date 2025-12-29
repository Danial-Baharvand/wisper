using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

public class TextInjector
{
    private readonly ILogger<TextInjector> _logger;
    private const int ClipboardDelayMs = 50;
    private const int PasteDelayMs = 150;

    public TextInjector(ILogger<TextInjector> logger)
    {
        _logger = logger;
    }

    public async Task InjectTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            _logger.LogDebug("No text to inject");
            return;
        }

        _logger.LogInformation("Injecting text, length: {Length} chars", text.Length);

        object? originalClipboard = null;
        bool hadOriginalContent = false;

        try
        {
            // Save original clipboard
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        originalClipboard = Clipboard.GetText();
                        hadOriginalContent = true;
                    }
                }
                catch { }
            });

            await Task.Delay(ClipboardDelayMs, cancellationToken);

            // Set clipboard to our text
            bool clipboardSet = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Clipboard.SetText(text);
                        clipboardSet = true;
                        break;
                    }
                    catch (ExternalException)
                    {
                        Thread.Sleep(50);
                    }
                }
            });

            if (!clipboardSet)
            {
                _logger.LogError("Failed to set clipboard");
                return;
            }

            await Task.Delay(ClipboardDelayMs, cancellationToken);

            // Send Ctrl+V using keybd_event (more compatible than SendInput)
            _logger.LogDebug("Sending Ctrl+V paste command");
            
            // Press Ctrl
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            await Task.Delay(10, cancellationToken);
            
            // Press V
            keybd_event(VK_V, 0, 0, UIntPtr.Zero);
            await Task.Delay(10, cancellationToken);
            
            // Release V
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(10, cancellationToken);
            
            // Release Ctrl
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            _logger.LogDebug("Paste command sent");

            await Task.Delay(PasteDelayMs, cancellationToken);

            // Restore original clipboard
            if (hadOriginalContent && originalClipboard is string origText)
            {
                await Task.Delay(200, cancellationToken);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try { Clipboard.SetText(origText); }
                    catch { }
                });
                _logger.LogDebug("Original clipboard restored");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject text");
            throw;
        }
    }

    #region Native Methods

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    #endregion
}