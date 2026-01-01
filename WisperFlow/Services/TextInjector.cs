using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

public class TextInjector
{
    private readonly ILogger<TextInjector> _logger;
    private const int ClipboardDelayMs = 30;   // Delay after setting clipboard
    private const int PasteDelayMs = 100;      // Delay after paste for app to process
    private const int TabDelayMs = 200;        // Delay after Tab for autocomplete to process

    public TextInjector(ILogger<TextInjector> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Injects text with @ mention support using "fast grab" clipboard strategy.
    /// 
    /// Fast grab strategy:
    /// 1. Use Win32 APIs to set clipboard and immediately re-grab it after paste
    /// 2. This minimizes the window where other apps can lock the clipboard
    /// 3. We "guard" the clipboard by keeping it open between operations
    /// </summary>
    public async Task InjectTextWithMentionsAsync(string text, IEnumerable<string>? knownFileNames, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            _logger.LogDebug("No text to inject");
            return;
        }
        
        // If no known names, just inject normally (clipboard)
        if (knownFileNames == null || !knownFileNames.Any())
        {
            await InjectTextAsync(text, cancellationToken);
            return;
        }
        
        // Build a set for fast lookup (case-insensitive)
        var nameSet = new HashSet<string>(knownFileNames, StringComparer.OrdinalIgnoreCase);
        
        // Log known files for debugging
        _logger.LogDebug("Known files for matching: {Files}", string.Join(", ", nameSet.Take(20)));
        
        // Find @ mentions and check which ones match known filenames
        var mentions = FindMentionSegments(text, nameSet);
        
        // Log what was found vs matched
        var allMentions = Regex.Matches(text, @"@([\w\-]+\.[\w]+)");
        if (allMentions.Count > mentions.Count)
        {
            var unmatched = allMentions.Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(f => !nameSet.Contains(f))
                .ToList();
            _logger.LogDebug("Unmatched @ mentions (not in known files): {Files}", string.Join(", ", unmatched));
        }
        
        if (mentions.Count == 0)
        {
            // No known @ mentions found, inject normally (clipboard)
            await InjectTextAsync(text, cancellationToken);
            return;
        }
        
        _logger.LogInformation("Injecting text with {Count} known file @ mentions (fast-grab clipboard strategy)", 
            mentions.Count);
        
        int lastIndex = 0;
        
        for (int i = 0; i < mentions.Count; i++)
        {
            var mention = mentions[i];
            int nextLastIndex = mention.AtIndex + 1 + mention.Filename.Length;
            
            // Get text from last position up to AND INCLUDING the @
            string textWithAt = text.Substring(lastIndex, mention.AtIndex - lastIndex + 1);
            string filename = mention.Filename;
            
            // Step 1: Paste "text@" using fast-grab
            if (!string.IsNullOrEmpty(textWithAt))
            {
                await FastGrabPasteAsync(textWithAt, cancellationToken);
                _logger.LogDebug("FastGrab pasted text ending with '@' ({Len} chars)", textWithAt.Length);
            }
            
            // Step 2: Paste the filename using fast-grab
            await FastGrabPasteAsync(filename, cancellationToken);
            _logger.LogDebug("FastGrab pasted filename '{Name}'", filename);
            
            // Step 3: Press Tab to confirm autocomplete
            await SendTabAsync(cancellationToken);
            await Task.Delay(TabDelayMs, cancellationToken);
            _logger.LogDebug("Pressed Tab to confirm autocomplete for '{Name}'", filename);
            
            lastIndex = nextLastIndex;
        }
        
        // Paste any remaining text after the last mention
        if (lastIndex < text.Length)
        {
            string remaining = text.Substring(lastIndex);
            if (!string.IsNullOrEmpty(remaining))
            {
                await FastGrabPasteAsync(remaining, cancellationToken);
                _logger.LogDebug("FastGrab pasted remaining text ({Len} chars)", remaining.Length);
            }
        }
    }
    
    /// <summary>
    /// Fast-grab paste: Sets clipboard, pastes, and immediately re-grabs clipboard.
    /// Uses Win32 APIs for precise control over clipboard timing.
    /// </summary>
    private async Task FastGrabPasteAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
            return;
        
        // Step 1: Set clipboard using Win32 API
        bool setSuccess = SetClipboardWin32(text);
        if (!setSuccess)
        {
            _logger.LogError("FastGrab: Failed to set clipboard for '{Text}'", 
                text.Length > 20 ? text.Substring(0, 20) + "..." : text);
            return;
        }
        
        // Step 2: Very brief delay before paste
        await Task.Delay(10, cancellationToken);
        
        // Step 3: Send Ctrl+V (paste)
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        
        // Step 4: IMMEDIATELY try to re-grab clipboard to block other apps
        // Try multiple times in quick succession
        for (int grab = 0; grab < 3; grab++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                // We got it! Hold it briefly then release
                await Task.Delay(20, cancellationToken);
                CloseClipboard();
                break;
            }
            await Task.Delay(5, cancellationToken);
        }
        
        // Step 5: Delay for paste to complete in target app
        await Task.Delay(PasteDelayMs, cancellationToken);
    }
    
    /// <summary>
    /// Sets clipboard text using Win32 APIs for more control.
    /// </summary>
    private bool SetClipboardWin32(string text)
    {
        const uint CF_UNICODETEXT = 13;
        
        // Retry loop
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20);
                continue;
            }
            
            try
            {
                EmptyClipboard();
                
                // Allocate global memory for the string
                int bytes = (text.Length + 1) * 2; // Unicode = 2 bytes per char
                IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hGlobal == IntPtr.Zero)
                {
                    CloseClipboard();
                    continue;
                }
                
                IntPtr pGlobal = GlobalLock(hGlobal);
                if (pGlobal == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    CloseClipboard();
                    continue;
                }
                
                // Copy string to global memory
                Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                Marshal.WriteInt16(pGlobal, text.Length * 2, 0); // Null terminator
                
                GlobalUnlock(hGlobal);
                
                // Set clipboard data (clipboard takes ownership of hGlobal)
                IntPtr result = SetClipboardData(CF_UNICODETEXT, hGlobal);
                CloseClipboard();
                
                if (result != IntPtr.Zero)
                {
                    return true; // Success!
                }
                
                // SetClipboardData failed, free the memory
                GlobalFree(hGlobal);
            }
            catch
            {
                CloseClipboard();
            }
            
            Thread.Sleep(20);
        }
        
        _logger.LogWarning("SetClipboardWin32 failed after 10 attempts");
        return false;
    }
    
    /// <summary>
    /// Finds all @ mentions in text where the filename matches a known name.
    /// Returns list of (AtIndex, Filename) for each match.
    /// </summary>
    private List<MentionInfo> FindMentionSegments(string text, HashSet<string> knownNames)
    {
        var results = new List<MentionInfo>();
        
        // Pattern: @ followed by filename with extension (e.g., @cli.py, @pipelines.yaml)
        var pattern = @"@([\w\-]+\.[\w]+)";
        var matches = Regex.Matches(text, pattern);
        
        foreach (Match match in matches)
        {
            string filename = match.Groups[1].Value;
            
            // Only include if filename is in the known list
            if (knownNames.Contains(filename))
            {
                results.Add(new MentionInfo
                {
                    AtIndex = match.Index,
                    Filename = filename
                });
            }
        }
        
        return results;
    }
    
    private class MentionInfo
    {
        public int AtIndex { get; set; }
        public string Filename { get; set; } = "";
    }
    
    /// <summary>
    /// Sends a Tab keypress.
    /// </summary>
    private async Task SendTabAsync(CancellationToken cancellationToken)
    {
        keybd_event(VK_TAB, 0, 0, UIntPtr.Zero);
        await Task.Delay(5, cancellationToken);
        keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
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
            await Task.Delay(5, cancellationToken);
            
            // Press V
            keybd_event(VK_V, 0, 0, UIntPtr.Zero);
            await Task.Delay(5, cancellationToken);
            
            // Release V
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(5, cancellationToken);
            
            // Release Ctrl
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            _logger.LogDebug("Paste command sent");

            await Task.Delay(PasteDelayMs, cancellationToken);

            // Restore original clipboard
            if (hadOriginalContent && originalClipboard is string origText)
            {
                await Task.Delay(100, cancellationToken);  // Reduced from 200ms
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
    private const byte VK_TAB = 0x09;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    
    // Clipboard APIs
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    
    // Memory APIs for clipboard
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    #endregion
}