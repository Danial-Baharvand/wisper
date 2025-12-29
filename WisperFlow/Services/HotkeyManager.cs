using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services;

/// <summary>
/// Manages global hotkey registration and low-level keyboard hooks for press/release detection.
/// Uses a combination of RegisterHotKey for the press and low-level hooks for release detection.
/// </summary>
public class HotkeyManager : IDisposable
{
    private readonly ILogger<HotkeyManager> _logger;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookCallback;
    private HwndSource? _hwndSource;
    private IntPtr _windowHandle;
    private bool _isRecording;
    private HotkeyModifiers _currentModifiers;
    private int _currentKey;
    private bool _disposed;

    // Track which modifier keys are currently pressed
    private bool _ctrlPressed;
    private bool _winPressed;
    private bool _altPressed;
    private bool _shiftPressed;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_HOTKEY = 0x0312;

    // Virtual key codes
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_LMENU = 0xA4;  // Left Alt
    private const int VK_RMENU = 0xA5;  // Right Alt
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;

    public event EventHandler? RecordStart;
    public event EventHandler? RecordStop;

    public HotkeyManager(ILogger<HotkeyManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Register the hotkey combination to listen for.
    /// </summary>
    public void RegisterHotkey(HotkeyModifiers modifiers, int key)
    {
        _currentModifiers = modifiers;
        _currentKey = key;

        // Install low-level keyboard hook for press/release detection
        InstallHook();

        _logger.LogInformation("Hotkey registered: Modifiers={Modifiers}, Key={Key}", modifiers, key);
    }

    /// <summary>
    /// Unregister the current hotkey.
    /// </summary>
    public void UnregisterHotkey()
    {
        UninstallHook();
        _logger.LogInformation("Hotkey unregistered");
    }

    private void InstallHook()
    {
        if (_hookId != IntPtr.Zero) return;

        _hookCallback = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, 
            GetModuleHandle(curModule.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogError("Failed to install keyboard hook, error code: {Error}", error);
        }
        else
        {
            _logger.LogDebug("Keyboard hook installed");
        }
    }

    private void UninstallHook()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _logger.LogDebug("Keyboard hook uninstalled");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool isKeyDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isKeyUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

            // Track modifier key states
            if (isKeyDown)
            {
                UpdateModifierState(vkCode, true);
            }
            else if (isKeyUp)
            {
                UpdateModifierState(vkCode, false);
            }

            // Check if our hotkey combination is matched
            CheckHotkeyState();
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void UpdateModifierState(int vkCode, bool pressed)
    {
        switch (vkCode)
        {
            case VK_LCONTROL:
            case VK_RCONTROL:
                _ctrlPressed = pressed;
                break;
            case VK_LWIN:
            case VK_RWIN:
                _winPressed = pressed;
                break;
            case VK_LMENU:
            case VK_RMENU:
                _altPressed = pressed;
                break;
            case VK_LSHIFT:
            case VK_RSHIFT:
                _shiftPressed = pressed;
                break;
        }
    }

    private void CheckHotkeyState()
    {
        bool modifiersMatch = true;

        // Check each required modifier
        if (_currentModifiers.HasFlag(HotkeyModifiers.Control) && !_ctrlPressed)
            modifiersMatch = false;
        if (_currentModifiers.HasFlag(HotkeyModifiers.Win) && !_winPressed)
            modifiersMatch = false;
        if (_currentModifiers.HasFlag(HotkeyModifiers.Alt) && !_altPressed)
            modifiersMatch = false;
        if (_currentModifiers.HasFlag(HotkeyModifiers.Shift) && !_shiftPressed)
            modifiersMatch = false;

        // Also verify no extra modifiers are pressed (unless we don't care)
        if (!_currentModifiers.HasFlag(HotkeyModifiers.Control) && _ctrlPressed)
            modifiersMatch = false;
        if (!_currentModifiers.HasFlag(HotkeyModifiers.Win) && _winPressed)
            modifiersMatch = false;
        // Allow Alt and Shift to be pressed even if not required

        if (modifiersMatch && !_isRecording)
        {
            // Start recording
            _isRecording = true;
            _logger.LogDebug("Hotkey pressed - starting recording");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RecordStart?.Invoke(this, EventArgs.Empty);
            });
        }
        else if (!modifiersMatch && _isRecording)
        {
            // Stop recording
            _isRecording = false;
            _logger.LogDebug("Hotkey released - stopping recording");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RecordStop?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UninstallHook();
        _hwndSource?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Native Methods

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, 
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    #endregion
}

