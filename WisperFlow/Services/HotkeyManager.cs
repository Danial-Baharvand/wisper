using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services;

/// <summary>
/// Manages global hotkey registration and low-level keyboard hooks for press/release detection.
/// Uses polling with GetAsyncKeyState for reliable modifier detection.
/// Supports two modes: Regular Dictation and Command Mode.
/// </summary>
public class HotkeyManager : IDisposable
{
    private readonly ILogger<HotkeyManager> _logger;
    private HotkeyModifiers _currentModifiers;       // Regular dictation (default: Ctrl+Win)
    private HotkeyModifiers _commandModifiers;       // Command mode (default: Ctrl+Win+Alt)
    private bool _disposed;
    private bool _commandModeEnabled = true;
    
    // Polling-based detection
    private System.Threading.Timer? _pollTimer;
    private bool _isRecording;
    private RecordingMode _currentRecordingMode = RecordingMode.None;
    private DateTime _modifiersFirstDetected;
    private DateTime _releaseFirstDetected;  // Track when release started
    private const int PollIntervalMs = 15;       // Fast polling
    private const int CommandHoldThresholdMs = 0;  // Command mode is instant (most specific - 3 keys)
    private const int RegularHoldThresholdMs = 60; // Regular mode waits a bit (in case user is pressing more keys)
    private const int ReleaseThresholdMs = 80;   // Time keys must be released before stopping

    // Virtual key codes
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_LMENU = 0xA4;  // Left Alt
    private const int VK_RMENU = 0xA5;  // Right Alt
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    
    private enum RecordingMode { None, Regular, Command }

    public event EventHandler? RecordStart;
    public event EventHandler? RecordStop;
    public event EventHandler? CommandRecordStart;
    public event EventHandler? CommandRecordStop;

    public HotkeyManager(ILogger<HotkeyManager> logger)
    {
        _logger = logger;
    }

    public void RegisterHotkey(HotkeyModifiers modifiers, int key)
    {
        _currentModifiers = modifiers;
        
        // Start polling timer
        _pollTimer?.Dispose();
        _pollTimer = new System.Threading.Timer(PollKeyStates, null, 0, PollIntervalMs);

        _logger.LogInformation("Hotkey registered: Modifiers={Modifiers}, Key={Key}", modifiers, key);
    }

    public void RegisterCommandHotkey(HotkeyModifiers modifiers, bool enabled)
    {
        _commandModifiers = modifiers;
        _commandModeEnabled = enabled;
        _logger.LogInformation("Command hotkey registered: Modifiers={Modifiers}, Enabled={Enabled}", modifiers, enabled);
    }

    public void UnregisterHotkey()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _logger.LogInformation("Hotkey unregistered");
    }

    private void PollKeyStates(object? state)
    {
        if (_disposed) return;

        try
        {
            // Get current state of all modifier keys using GetAsyncKeyState
            bool ctrlPressed = IsKeyPressed(VK_LCONTROL) || IsKeyPressed(VK_RCONTROL);
            bool winPressed = IsKeyPressed(VK_LWIN) || IsKeyPressed(VK_RWIN);
            bool altPressed = IsKeyPressed(VK_LMENU) || IsKeyPressed(VK_RMENU);
            bool shiftPressed = IsKeyPressed(VK_LSHIFT) || IsKeyPressed(VK_RSHIFT);

            // Build current modifiers
            HotkeyModifiers currentModifiers = HotkeyModifiers.None;
            if (ctrlPressed) currentModifiers |= HotkeyModifiers.Control;
            if (winPressed) currentModifiers |= HotkeyModifiers.Win;
            if (altPressed) currentModifiers |= HotkeyModifiers.Alt;
            if (shiftPressed) currentModifiers |= HotkeyModifiers.Shift;

            // Check for matches - priority: Command (3 keys) > Regular (2 keys)
            bool commandMatch = _commandModeEnabled && MatchesModifiers(currentModifiers, _commandModifiers);
            bool regularMatch = MatchesModifiers(currentModifiers, _currentModifiers);

            // Handle state transitions
            if (_isRecording)
            {
                // Currently recording - check if should stop
                bool shouldContinue = _currentRecordingMode switch
                {
                    RecordingMode.Command => HasMostModifiers(currentModifiers, _commandModifiers),
                    RecordingMode.Regular => regularMatch,
                    _ => false
                };
                
                if (shouldContinue)
                {
                    // Keys still held - reset release timer
                    _releaseFirstDetected = DateTime.MinValue;
                }
                else
                {
                    // Keys released - check if sustained release
                    if (_releaseFirstDetected == DateTime.MinValue)
                    {
                        _releaseFirstDetected = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - _releaseFirstDetected).TotalMilliseconds >= ReleaseThresholdMs)
                    {
                        // Sustained release - stop recording
                        var previousMode = _currentRecordingMode;
                        _isRecording = false;
                        _currentRecordingMode = RecordingMode.None;
                        _releaseFirstDetected = DateTime.MinValue;

                        switch (previousMode)
                        {
                            case RecordingMode.Command:
                                _logger.LogDebug("Command hotkey released - stopping command recording");
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                                {
                                    CommandRecordStop?.Invoke(this, EventArgs.Empty);
                                });
                                break;
                            case RecordingMode.Regular:
                                _logger.LogDebug("Hotkey released - stopping recording");
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                                {
                                    RecordStop?.Invoke(this, EventArgs.Empty);
                                });
                                break;
                        }
                    }
                }
            }
            else
            {
                // Not recording - check if should start (priority order)
                if (commandMatch)
                {
                    // Command mode - start instantly (most specific: 3 keys)
                    _isRecording = true;
                    _currentRecordingMode = RecordingMode.Command;
                    _releaseFirstDetected = DateTime.MinValue;
                    _modifiersFirstDetected = DateTime.MinValue;
                    _logger.LogDebug("Command hotkey pressed - starting command recording");
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        CommandRecordStart?.Invoke(this, EventArgs.Empty);
                    });
                }
                else if (regularMatch)
                {
                    // Regular mode - wait a bit in case user is adding more modifiers
                    if (_modifiersFirstDetected == DateTime.MinValue)
                    {
                        _modifiersFirstDetected = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - _modifiersFirstDetected).TotalMilliseconds >= RegularHoldThresholdMs)
                    {
                        _isRecording = true;
                        _currentRecordingMode = RecordingMode.Regular;
                        _releaseFirstDetected = DateTime.MinValue;
                        _logger.LogDebug("Hotkey pressed - starting recording");
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            RecordStart?.Invoke(this, EventArgs.Empty);
                        });
                    }
                }
                else
                {
                    // No match - reset hold timer
                    _modifiersFirstDetected = DateTime.MinValue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling key states");
        }
    }

    /// <summary>
    /// Check if at least N-1 of the required modifiers are pressed (lenient matching for release detection)
    /// </summary>
    private static bool HasMostModifiers(HotkeyModifiers current, HotkeyModifiers required)
    {
        int requiredCount = 0;
        int matchedCount = 0;

        if (required.HasFlag(HotkeyModifiers.Control))
        {
            requiredCount++;
            if (current.HasFlag(HotkeyModifiers.Control)) matchedCount++;
        }
        if (required.HasFlag(HotkeyModifiers.Win))
        {
            requiredCount++;
            if (current.HasFlag(HotkeyModifiers.Win)) matchedCount++;
        }
        if (required.HasFlag(HotkeyModifiers.Alt))
        {
            requiredCount++;
            if (current.HasFlag(HotkeyModifiers.Alt)) matchedCount++;
        }
        if (required.HasFlag(HotkeyModifiers.Shift))
        {
            requiredCount++;
            if (current.HasFlag(HotkeyModifiers.Shift)) matchedCount++;
        }

        // For 3 keys, require at least 2. For 2 keys, require at least 1. For 1 key, require 1.
        int minRequired = Math.Max(1, requiredCount - 1);
        return matchedCount >= minRequired;
    }

    private static bool MatchesModifiers(HotkeyModifiers current, HotkeyModifiers required)
    {
        if (required == HotkeyModifiers.None) return false;
        
        // Check all required modifiers are pressed
        if (required.HasFlag(HotkeyModifiers.Control) && !current.HasFlag(HotkeyModifiers.Control))
            return false;
        if (required.HasFlag(HotkeyModifiers.Win) && !current.HasFlag(HotkeyModifiers.Win))
            return false;
        if (required.HasFlag(HotkeyModifiers.Alt) && !current.HasFlag(HotkeyModifiers.Alt))
            return false;
        if (required.HasFlag(HotkeyModifiers.Shift) && !current.HasFlag(HotkeyModifiers.Shift))
            return false;

        // Check no extra modifiers are pressed (except Shift which is always allowed)
        if (!required.HasFlag(HotkeyModifiers.Control) && current.HasFlag(HotkeyModifiers.Control))
            return false;
        if (!required.HasFlag(HotkeyModifiers.Win) && current.HasFlag(HotkeyModifiers.Win))
            return false;
        if (!required.HasFlag(HotkeyModifiers.Alt) && current.HasFlag(HotkeyModifiers.Alt))
            return false;

        return true;
    }

    private static bool IsKeyPressed(int vk)
    {
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
