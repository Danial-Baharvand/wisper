using WisperFlow.Models;
using Xunit;

namespace WisperFlow.Tests;

/// <summary>
/// Tests for hotkey parsing and formatting.
/// </summary>
public class HotkeyParserTests
{
    [Fact]
    public void HotkeyModifiers_CanCombineFlags()
    {
        // Arrange & Act
        var modifiers = HotkeyModifiers.Control | HotkeyModifiers.Win;

        // Assert
        Assert.True(modifiers.HasFlag(HotkeyModifiers.Control));
        Assert.True(modifiers.HasFlag(HotkeyModifiers.Win));
        Assert.False(modifiers.HasFlag(HotkeyModifiers.Alt));
        Assert.False(modifiers.HasFlag(HotkeyModifiers.Shift));
    }

    [Fact]
    public void HotkeyModifiers_DefaultIsCtrlWin()
    {
        // Arrange
        var settings = new AppSettings();

        // Assert
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Win, settings.HotkeyModifiers);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, "Ctrl")]
    [InlineData(HotkeyModifiers.Alt, "Alt")]
    [InlineData(HotkeyModifiers.Shift, "Shift")]
    [InlineData(HotkeyModifiers.Win, "Win")]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Win, "Ctrl + Win")]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "Ctrl + Alt + Shift")]
    public void FormatHotkey_FormatsCorrectly(HotkeyModifiers modifiers, string expected)
    {
        // Arrange & Act
        var result = FormatHotkey(modifiers);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void HotkeyModifiers_NoneFormatsAsNone()
    {
        // Arrange & Act
        var result = FormatHotkey(HotkeyModifiers.None);

        // Assert
        Assert.Equal("None", result);
    }

    /// <summary>
    /// Helper method matching the formatting logic in SettingsWindow.
    /// </summary>
    private static string FormatHotkey(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();

        if (modifiers.HasFlag(HotkeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win))
            parts.Add("Win");

        return parts.Count > 0 ? string.Join(" + ", parts) : "None";
    }
}

