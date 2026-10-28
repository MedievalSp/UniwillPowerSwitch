namespace UwPower.Core;

/// <summary>Modifier flags; values match RegisterHotKey's MOD_* constants.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global keyboard shortcut such as "Ctrl+Alt+Shift+1".</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly Dictionary<string, int> NamedKeys = BuildKeyTable();

    public static bool TryParse(string? text, out Hotkey hotkey, out string? error)
    {
        hotkey = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "No shortcut given.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Ctrl;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            if (key is not null)
            {
                error = $"'{text}' has more than one key; use modifiers plus one key, e.g. Ctrl+Alt+Shift+1.";
                return false;
            }
            if (!NamedKeys.TryGetValue(part.ToUpperInvariant(), out var vk))
            {
                error = $"'{part}' is not a supported key (use 0-9, A-Z, F1-F24, Num0-Num9, arrows, Home, End...).";
                return false;
            }
            key = vk;
        }

        if (key is null)
        {
            error = $"'{text}' has no key, only modifiers.";
            return false;
        }
        // Shift alone would steal ordinary typing (e.g. Shift+1 is "!").
        if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            error = $"'{text}' needs Ctrl, Alt or Win so it doesn't interfere with typing.";
            return false;
        }

        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }

    /// <summary>Display name for a virtual-key code, or null if it can't be used as a shortcut key.</summary>
    public static string? KeyName(int virtualKey) =>
        NamedKeys.FirstOrDefault(pair => pair.Value == virtualKey).Key is { } name ? Pretty(name) : null;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win))
            parts.Add("Win");
        parts.Add(KeyName(VirtualKey) ?? $"0x{VirtualKey:X2}");
        return string.Join("+", parts);
    }

    private static string Pretty(string upper) => upper switch
    {
        "PAGEUP" => "PageUp",
        "PAGEDOWN" => "PageDown",
        _ when upper.StartsWith("NUM") && upper.Length == 4 => "Num" + upper[3],
        _ when upper.Length > 1 && !upper.StartsWith('F') => upper[0] + upper[1..].ToLowerInvariant(),
        _ => upper,
    };

    private static Dictionary<string, int> BuildKeyTable()
    {
        var keys = new Dictionary<string, int>
        {
            ["SPACE"] = 0x20,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["END"] = 0x23,
            ["HOME"] = 0x24,
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
            ["INSERT"] = 0x2D,
            ["DELETE"] = 0x2E,
        };
        for (var i = 0; i <= 9; i++)
        {
            keys[i.ToString()] = 0x30 + i;
            keys[$"NUM{i}"] = 0x60 + i;
        }
        for (var c = 'A'; c <= 'Z'; c++)
            keys[c.ToString()] = c;
        for (var i = 1; i <= 24; i++)
            keys[$"F{i}"] = 0x6F + i;
        return keys;
    }
}
