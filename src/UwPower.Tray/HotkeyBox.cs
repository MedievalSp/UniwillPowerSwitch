using UwPower.Core;

namespace UwPower.Tray;

/// <summary>A text box that records a shortcut when you press it. Backspace or Delete clears it.</summary>
internal sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        ShortcutsEnabled = false;
        Width = 190;
        PlaceholderText = "None";
    }

    /// <summary>The recorded shortcut ("Ctrl+Alt+Shift+1"), or null when cleared.</summary>
    public string? Shortcut
    {
        get => string.IsNullOrEmpty(Text) ? null : Text;
        set => Text = value ?? "";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        if (e.Modifiers == Keys.None && e.KeyCode is Keys.Back or Keys.Delete or Keys.Escape)
        {
            if (e.KeyCode != Keys.Escape)
                Shortcut = null;
            return;
        }
        if (e.KeyCode == Keys.Tab && e.Modifiers is Keys.None or Keys.Shift)
        {
            Parent?.SelectNextControl(this, e.Modifiers == Keys.None, true, true, true);
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (e.Control)
            modifiers |= HotkeyModifiers.Ctrl;
        if (e.Alt)
            modifiers |= HotkeyModifiers.Alt;
        if (e.Shift)
            modifiers |= HotkeyModifiers.Shift;
        if (IsKeyDown(Keys.LWin) || IsKeyDown(Keys.RWin)) // WinForms doesn't report Win as a modifier
            modifiers |= HotkeyModifiers.Win;

        // Wait for a real key; modifiers alone don't make a shortcut.
        if (Hotkey.KeyName((int)e.KeyCode) is null)
            return;

        var candidate = new Hotkey(modifiers, (int)e.KeyCode).ToString();
        if (Hotkey.TryParse(candidate, out _, out _))
            Shortcut = candidate;
    }

    protected override void OnKeyPress(KeyPressEventArgs e) => e.Handled = true;

    // Let Alt+key and Tab reach OnKeyDown instead of being treated as dialog navigation.
    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessDialogKey(Keys keyData) => false;

    private static bool IsKeyDown(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
