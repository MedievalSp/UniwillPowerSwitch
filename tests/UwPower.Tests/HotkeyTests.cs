using UwPower.Core;
using UwPower.Core.Config;

namespace UwPower.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Shift+1", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x31, "Ctrl+Alt+Shift+1")]
    [InlineData("shift + control + alt + 3", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x33, "Ctrl+Alt+Shift+3")]
    [InlineData("Win+Alt+F9", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x78, "Alt+Win+F9")]
    [InlineData("ctrl+b", HotkeyModifiers.Ctrl, 0x42, "Ctrl+B")]
    [InlineData("Ctrl+Alt+num7", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x67, "Ctrl+Alt+Num7")]
    [InlineData("Ctrl+Alt+pageup", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x21, "Ctrl+Alt+PageUp")]
    public void ParsesAndFormats(string text, HotkeyModifiers modifiers, int vk, string formatted)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey, out var error), error);
        Assert.Equal(new Hotkey(modifiers, vk), hotkey);
        Assert.Equal(formatted, hotkey.ToString());
        Assert.True(Hotkey.TryParse(formatted, out var again, out _));
        Assert.Equal(hotkey, again);
    }

    [Theory]
    [InlineData("", "No shortcut")]
    [InlineData("1", "needs Ctrl, Alt or Win")]
    [InlineData("Shift+1", "needs Ctrl, Alt or Win")]
    [InlineData("Ctrl+Alt", "no key")]
    [InlineData("Ctrl+1+2", "more than one key")]
    [InlineData("Ctrl+Banana", "not a supported key")]
    public void RejectsInvalid(string text, string errorFragment)
    {
        Assert.False(Hotkey.TryParse(text, out _, out var error));
        Assert.Contains(errorFragment, error);
    }

    [Fact]
    public void KeyNamesCoverRecordableKeys()
    {
        Assert.Equal("1", Hotkey.KeyName(0x31));
        Assert.Equal("F12", Hotkey.KeyName(0x7B));
        Assert.Equal("Space", Hotkey.KeyName(0x20));
        Assert.Null(Hotkey.KeyName(0x10)); // Shift on its own
    }

    [Fact]
    public void DefaultsAreCtrlAltShiftDigits()
    {
        var settings = new HotkeySettings();
        Assert.True(settings.Enabled);
        Assert.Equal("Ctrl+Alt+Shift+1", settings.For(PowerMode.Balanced));
        Assert.Equal("Ctrl+Alt+Shift+2", settings.For(PowerMode.Enthusiast));
        Assert.Equal("Ctrl+Alt+Shift+3", settings.For(PowerMode.Beast));
        Assert.Null(settings.Validate());
    }

    [Fact]
    public void ValidationCatchesDuplicatesButAllowsEmpty()
    {
        var settings = new HotkeySettings { Enthusiast = null, Beast = "ctrl+alt+shift+1" };
        Assert.Contains("same shortcut", settings.Validate());
        settings.Beast = null;
        Assert.Null(settings.Validate());
    }
}
