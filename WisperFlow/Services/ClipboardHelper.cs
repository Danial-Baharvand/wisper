using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

/// <summary>
/// Helper for clipboard operations and getting selected text from active applications.
/// </summary>
public static class ClipboardHelper
{
    private static ILogger? _logger;
    
    public static void SetLogger(ILogger logger) => _logger = logger;

    /// <summary>
    /// Gets the currently selected text by simulating Ctrl+C and reading clipboard.
    /// Returns the selected text, or empty string if nothing selected.
    /// </summary>
    public static async Task<string> GetSelectedTextAsync()
    {
        // Wait for modifier keys to be released (important!)
        await WaitForModifiersReleasedAsync();
        
        // Save current clipboard contents
        string? previousClipboard = null;
        try
        {
            if (Clipboard.ContainsText())
            {
                previousClipboard = Clipboard.GetText();
            }
        }
        catch
        {
            // Clipboard might be locked by another app
        }

        try
        {
            // Clear clipboard
            Clipboard.Clear();
            await Task.Delay(50);

            // Simulate Ctrl+C to copy selected text using SendInput
            SendCtrlC();
            await Task.Delay(150); // Wait for the copy operation

            // Get the copied text
            string selectedText = "";
            
            // Try multiple times as clipboard might be locked
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        selectedText = Clipboard.GetText();
                        break;
                    }
                }
                catch
                {
                    await Task.Delay(50);
                }
            }

            if (string.IsNullOrEmpty(selectedText))
            {
                _logger?.LogWarning("GetSelectedTextAsync: clipboard was empty after Ctrl+C");
            }
            else
            {
                _logger?.LogDebug("GetSelectedTextAsync: got {Len} chars", selectedText.Length);
            }
            return selectedText;
        }
        finally
        {
            // Restore previous clipboard contents (but don't if we got text - we'll paste soon)
            if (previousClipboard != null && string.IsNullOrEmpty(await Task.FromResult("")))
            {
                try
                {
                    await Task.Delay(50);
                    Clipboard.SetText(previousClipboard);
                }
                catch
                {
                    // Best effort
                }
            }
        }
    }

    /// <summary>
    /// Replaces the currently selected text with new text by simulating paste.
    /// </summary>
    public static async Task ReplaceSelectedTextAsync(string newText)
    {
        if (string.IsNullOrEmpty(newText)) return;

        // Wait for any modifier keys to be released
        await WaitForModifiersReleasedAsync();

        try
        {
            // Set clipboard to new text
            Clipboard.SetText(newText);
            await Task.Delay(100);

            // Simulate Ctrl+V to paste
            SendCtrlV();
            await Task.Delay(100);
            
            _logger?.LogDebug("ReplaceSelectedTextAsync: pasted {Len} chars", newText.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to paste text");
        }
    }

    private static async Task WaitForModifiersReleasedAsync()
    {
        // Quick check - usually modifiers are already released when we're called
        for (int i = 0; i < 20; i++)  // Max 200ms wait
        {
            bool anyPressed = 
                (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            if (!anyPressed)
            {
                await Task.Delay(20); // Small delay after release
                return;
            }
            
            await Task.Delay(10);
        }
        
        _logger?.LogWarning("Modifiers still pressed after timeout");
    }

    private static void SendCtrlC()
    {
        var inputs = new INPUT[4];
        
        // Ctrl down
        inputs[0] = CreateKeyInput(VK_CONTROL, false);
        // C down
        inputs[1] = CreateKeyInput(0x43, false);
        // C up
        inputs[2] = CreateKeyInput(0x43, true);
        // Ctrl up
        inputs[3] = CreateKeyInput(VK_CONTROL, true);
        
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];
        
        // Ctrl down
        inputs[0] = CreateKeyInput(VK_CONTROL, false);
        // V down
        inputs[1] = CreateKeyInput(0x56, false);
        // V up
        inputs[2] = CreateKeyInput(0x56, true);
        // Ctrl up
        inputs[3] = CreateKeyInput(VK_CONTROL, true);
        
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT CreateKeyInput(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    #region Native Methods
    
    private const int INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12;     // Alt
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy, mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    #endregion
    
    /// <summary>
    /// Checks if the currently focused element is an ACTUAL editable text input field.
    /// Uses Windows UI Automation with strict checks to avoid false positives in browsers.
    /// Only returns true for genuine text inputs where you can type (caret blinking).
    /// </summary>
    public static bool IsTextInputFocused()
    {
        try
        {
            // Use UI Automation to get the focused element
            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement == null)
            {
                _logger?.LogDebug("UI Automation: No focused element");
                return false;
            }

            // Get element info for logging
            string controlTypeName = "unknown";
            string name = "";
            string className = "";
            string automationId = "";
            ControlType? controlType = null;
            
            try
            {
                controlType = focusedElement.Current.ControlType;
                controlTypeName = controlType?.ProgrammaticName ?? "unknown";
                name = focusedElement.Current.Name ?? "";
                className = focusedElement.Current.ClassName ?? "";
                automationId = focusedElement.Current.AutomationId ?? "";
            }
            catch { /* Ignore property access errors */ }

            // STRICT CHECK 1: Must be an Edit control type
            // This excludes Document (browser content), Pane, Window, etc.
            bool isEditControlType = controlType == ControlType.Edit;
            
            // STRICT CHECK 2: For ComboBox, also accept (dropdowns with text input)
            bool isComboBox = controlType == ControlType.ComboBox;
            
            // STRICT CHECK 3: Check if it supports VALUE pattern and is NOT read-only
            bool hasEditableValue = false;
            try
            {
                if (focusedElement.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern))
                {
                    var valuePattern = pattern as ValuePattern;
                    if (valuePattern != null)
                    {
                        hasEditableValue = !valuePattern.Current.IsReadOnly;
                    }
                }
            }
            catch { /* Ignore */ }

            // STRICT CHECK 4: For rich text, check if it's an actual editor
            // (not just a document viewer)
            bool isRichTextEditor = false;
            if (controlType == ControlType.Document)
            {
                // Only accept Document if it has specific edit-related class names
                var editClassNames = new[] { 
                    "edit", "richedit", "textbox", "input", "textarea",
                    "ace_editor", "monaco", "codemirror", // Code editors
                    "contenteditable", "ql-editor", // Rich text editors
                    "draft-editor", "prosemirror" // More rich text editors
                };
                var classLower = className.ToLowerInvariant();
                isRichTextEditor = editClassNames.Any(ec => classLower.Contains(ec));
            }

            // STRICT CHECK 5: Check class name for common text input patterns
            bool hasTextInputClassName = false;
            if (!string.IsNullOrEmpty(className))
            {
                var classLower = className.ToLowerInvariant();
                var textInputPatterns = new[] { 
                    "edit", "textbox", "input", "textarea", "richedit",
                    "searchbox", "addressbar", "urlbar", "omnibox"
                };
                hasTextInputClassName = textInputPatterns.Any(p => classLower.Contains(p));
            }

            // Final decision: Must be an Edit control with editable value
            // OR a ComboBox with editable value
            // OR a verified rich text editor
            bool isTextInput = (isEditControlType && hasEditableValue) ||
                              (isComboBox && hasEditableValue) ||
                              (isRichTextEditor && hasEditableValue) ||
                              (hasTextInputClassName && hasEditableValue);

            _logger?.LogDebug("UI Automation: Type={Type}, Class={Class}, Name={Name}, " +
                "IsEdit={IsEdit}, HasEditableValue={HasValue}, IsRichText={IsRich}, HasTextClass={HasClass} => Result={Result}",
                controlTypeName, 
                className.Length > 30 ? className[..30] + "..." : className,
                name.Length > 20 ? name[..20] + "..." : name,
                isEditControlType, hasEditableValue, isRichTextEditor, hasTextInputClassName,
                isTextInput);

            if (isTextInput)
            {
                _logger?.LogInformation("Text input CONFIRMED: {Type} ({Class})", controlTypeName, className);
            }

            return isTextInput;
        }
        catch (ElementNotAvailableException)
        {
            _logger?.LogDebug("UI Automation: Element not available (focus changed)");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "UI Automation failed, falling back to false");
            return false;
        }
    }
}
