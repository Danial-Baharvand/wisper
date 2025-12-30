using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Web;
using System.Windows;

namespace WisperFlow.Services;

/// <summary>
/// Opens queries in AI chat services in the default browser.
/// </summary>
public static class BrowserQueryService
{
    /// <summary>
    /// Opens a query in the specified AI service.
    /// For services that support URL-based queries (Perplexity, Google, Copilot), 
    /// uses direct URL. For others, opens the page and pastes+submits the query.
    /// </summary>
    public static async Task OpenQueryAsync(string query, string service)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        var serviceLower = service.ToLowerInvariant();
        
        // Services that support direct query URLs with auto-submit
        if (serviceLower is "perplexity" or "google" or "copilot")
        {
            var encodedQuery = HttpUtility.UrlEncode(query);
            var url = serviceLower switch
            {
                "perplexity" => $"https://www.perplexity.ai/search?q={encodedQuery}",
                "copilot" => $"https://copilot.microsoft.com/?q={encodedQuery}",
                "google" => $"https://www.google.com/search?q={encodedQuery}",
                _ => throw new InvalidOperationException()
            };
            
            OpenUrl(url);
        }
        else
        {
            // Services that need paste+enter (ChatGPT, Gemini, Claude)
            var url = serviceLower switch
            {
                "chatgpt" => "https://chat.openai.com/",
                "gemini" => "https://gemini.google.com/app",
                "claude" => "https://claude.ai/new",
                _ => "https://chat.openai.com/"
            };
            
            // Copy query to clipboard first
            try
            {
                Clipboard.SetText(query);
            }
            catch
            {
                // Clipboard might be locked
                return;
            }
            
            // Open the URL
            OpenUrl(url);
            
            // Wait for the page to load, then paste and submit
            await Task.Delay(2500); // Wait for page load
            
            // Paste (Ctrl+V)
            SendCtrlV();
            await Task.Delay(200);
            
            // Press Enter to submit
            SendEnter();
        }
    }

    /// <summary>
    /// Synchronous version for backward compatibility.
    /// </summary>
    public static void OpenQuery(string query, string service)
    {
        _ = Task.Run(() => OpenQueryAsync(query, service));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback: try with cmd
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c start \"\" \"{url}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];
        
        inputs[0] = CreateKeyInput(VK_CONTROL, false);
        inputs[1] = CreateKeyInput(0x56, false);  // V
        inputs[2] = CreateKeyInput(0x56, true);
        inputs[3] = CreateKeyInput(VK_CONTROL, true);
        
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendEnter()
    {
        var inputs = new INPUT[2];
        
        inputs[0] = CreateKeyInput(VK_RETURN, false);
        inputs[1] = CreateKeyInput(VK_RETURN, true);
        
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

    /// <summary>
    /// Available AI services for querying.
    /// </summary>
    public static IReadOnlyList<string> AvailableServices { get; } = new[]
    {
        "Perplexity",   // Moved to top as it works best
        "Google",
        "Copilot",
        "ChatGPT",
        "Gemini",
        "Claude"
    };

    #region Native Methods

    private const int INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_RETURN = 0x0D;
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

    #endregion
}
