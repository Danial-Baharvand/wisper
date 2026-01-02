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
    /// Uses exponential backoff: 15 attempts, ~1.5s total max wait.
    /// </summary>
    private bool SetClipboardWin32(string text)
    {
        const uint CF_UNICODETEXT = 13;
        const int MaxAttempts = 15;
        int delayMs = 20; // Starting delay, will double each time (exponential backoff)
        
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                // Log who's blocking the clipboard on first and every 5th failure
                if (attempt == 0 || attempt % 5 == 0)
                {
                    LogClipboardBlocker(attempt);
                }
                
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 200); // Cap at 200ms
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
                    if (attempt > 0)
                    {
                        _logger.LogDebug("Clipboard acquired after {Attempts} attempts", attempt + 1);
                    }
                    return true; // Success!
                }
                
                // SetClipboardData failed, free the memory
                GlobalFree(hGlobal);
            }
            catch
            {
                CloseClipboard();
            }
            
            Thread.Sleep(delayMs);
            delayMs = Math.Min(delayMs * 2, 200);
        }
        
        _logger.LogWarning("SetClipboardWin32 failed after {Attempts} attempts", MaxAttempts);
        LogClipboardBlocker(MaxAttempts);
        return false;
    }
    
    /// <summary>
    /// Logs information about which process is holding the clipboard.
    /// </summary>
    private void LogClipboardBlocker(int attempt)
    {
        try
        {
            IntPtr hwnd = GetOpenClipboardWindow();
            if (hwnd == IntPtr.Zero)
            {
                _logger.LogDebug("Clipboard blocked (attempt {Attempt}) but no window is holding it", attempt);
                return;
            }
            
            GetWindowThreadProcessId(hwnd, out int processId);
            if (processId > 0)
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(processId);
                    _logger.LogWarning("Clipboard blocked (attempt {Attempt}) by process: {ProcessName} (PID: {PID})", 
                        attempt, process.ProcessName, processId);
                }
                catch
                {
                    _logger.LogWarning("Clipboard blocked (attempt {Attempt}) by unknown process (PID: {PID})", 
                        attempt, processId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to identify clipboard blocker");
        }
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
    
    /// <summary>
    /// Checks if the Windows key (left or right) is currently pressed.
    /// </summary>
    private bool IsWindowsKeyPressed()
    {
        // GetAsyncKeyState returns negative (high bit set) if key is currently down
        return (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || 
               (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
    }
    
    /// <summary>
    /// Checks if the Control key is currently pressed.
    /// </summary>
    private bool IsControlKeyPressed()
    {
        return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
    }
    
    /// <summary>
    /// Waits for modifier keys (Ctrl+Win) to be released before allowing paste.
    /// Used when recording stops automatically (max duration) while user still holds hotkeys.
    /// </summary>
    /// <param name="timeoutSeconds">How long to wait before giving up</param>
    /// <param name="onWaitingStarted">Called when we start waiting (user is holding keys)</param>
    /// <returns>True if keys were released, false if timeout expired</returns>
    public async Task<bool> WaitForHotkeysReleasedAsync(int timeoutSeconds = 10, Action? onWaitingStarted = null)
    {
        // Quick check - if keys aren't held, return immediately
        if (!IsWindowsKeyPressed() && !IsControlKeyPressed())
        {
            _logger.LogDebug("Hotkeys not pressed, proceeding immediately");
            return true;
        }
        
        // User is holding keys - notify and wait
        _logger.LogInformation("Waiting for user to release hotkeys (Ctrl+Win)...");
        onWaitingStarted?.Invoke();
        
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        
        while (DateTime.UtcNow - startTime < timeout)
        {
            // Check every 50ms
            await Task.Delay(50);
            
            // Check if both modifier keys are released
            if (!IsWindowsKeyPressed() && !IsControlKeyPressed())
            {
                var elapsed = DateTime.UtcNow - startTime;
                _logger.LogInformation("Hotkeys released after {Elapsed:F1}s", elapsed.TotalSeconds);
                
                // Small delay to ensure key-up events are processed
                await Task.Delay(50);
                return true;
            }
        }
        
        _logger.LogWarning("Hotkey release timeout after {Timeout}s", timeoutSeconds);
        return false;
    }

    public async Task InjectTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            _logger.LogDebug("No text to inject");
            return;
        }

        _logger.LogInformation("Injecting text, length: {Length} chars", text.Length);

        string? originalClipboard = null;

        try
        {
            // Save original clipboard using Win32 for reliability
            originalClipboard = GetClipboardTextWin32();

            await Task.Delay(ClipboardDelayMs, cancellationToken);

            // Set clipboard using Win32 API with exponential backoff
            bool clipboardSet = SetClipboardWin32(text);

            if (!clipboardSet)
            {
                _logger.LogWarning("Clipboard unavailable, attempting SendInput fallback for {Len} chars", text.Length);
                
                // Fallback: Use SendInput to type the text directly
                if (text.Length <= SendInputMaxLength)
                {
                    await TypeTextViaSendInputAsync(text, cancellationToken);
                    _logger.LogInformation("Text injected via SendInput fallback");
                    return;
                }
                else
                {
                    _logger.LogError("Failed to set clipboard and text too long for SendInput fallback ({Len} chars)", text.Length);
                    return;
                }
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
            if (!string.IsNullOrEmpty(originalClipboard))
            {
                await Task.Delay(100, cancellationToken);
                SetClipboardWin32(originalClipboard);
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
    
    /// <summary>
    /// Gets clipboard text using Win32 APIs.
    /// </summary>
    private string? GetClipboardTextWin32()
    {
        const uint CF_UNICODETEXT = 13;
        
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20);
                continue;
            }
            
            try
            {
                IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                if (hData == IntPtr.Zero)
                {
                    CloseClipboard();
                    return null;
                }
                
                IntPtr pData = GlobalLock(hData);
                if (pData == IntPtr.Zero)
                {
                    CloseClipboard();
                    return null;
                }
                
                string? text = Marshal.PtrToStringUni(pData);
                GlobalUnlock(hData);
                CloseClipboard();
                return text;
            }
            catch
            {
                CloseClipboard();
            }
        }
        
        return null;
    }
    
    // Maximum text length for SendInput fallback (typing is slow, so limit it)
    private const int SendInputMaxLength = 500;
    
    /// <summary>
    /// Types text character by character using SendInput.
    /// Slower but works even when clipboard is locked.
    /// </summary>
    private async Task TypeTextViaSendInputAsync(string text, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Typing {Len} chars via SendInput", text.Length);
        
        foreach (char c in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Use KEYEVENTF_UNICODE to send the character directly
            var inputs = new INPUT[2];
            
            // Key down
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = 0;
            inputs[0].U.ki.wScan = c;
            inputs[0].U.ki.dwFlags = KEYEVENTF_UNICODE;
            inputs[0].U.ki.time = 0;
            inputs[0].U.ki.dwExtraInfo = UIntPtr.Zero;
            
            // Key up
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].U.ki.wVk = 0;
            inputs[1].U.ki.wScan = c;
            inputs[1].U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            inputs[1].U.ki.time = 0;
            inputs[1].U.ki.dwExtraInfo = UIntPtr.Zero;
            
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
            
            // Small delay between characters (1ms) to avoid overwhelming the target app
            await Task.Delay(1, cancellationToken);
        }
    }

    #region Native Methods

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const byte VK_TAB = 0x09;
    private const byte VK_LWIN = 0x5B;
    private const byte VK_RWIN = 0x5C;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int INPUT_KEYBOARD = 1;
    
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetOpenClipboardWindow();
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    
    // Memory APIs for clipboard
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
    
    // SendInput for fallback typing
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION U;
    }
    
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    #endregion
}