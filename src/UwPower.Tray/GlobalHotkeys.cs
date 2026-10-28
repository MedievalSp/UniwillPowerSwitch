using System.Runtime.InteropServices;
using UwPower.Core;
using UwPower.Core.Config;

namespace UwPower.Tray;

/// <summary>Registers system-wide shortcuts with RegisterHotKey and reports which mode was requested.</summary>
internal sealed partial class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly HashSet<int> _registered = [];
    private HotkeySettings _settings = new();
    private bool _suspended;

    public GlobalHotkeys() => CreateHandle(new CreateParams());

    public event Action<PowerMode>? Pressed;

    /// <summary>Shortcuts that could not be registered, with the reason.</summary>
    public IReadOnlyDictionary<PowerMode, string> Problems { get; private set; } = new Dictionary<PowerMode, string>();

    /// <summary>True while the settings window is recording a shortcut, so the keys reach it.</summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value)
                return;
            _suspended = value;
            Apply(_settings);
        }
    }

    public void Apply(HotkeySettings settings)
    {
        _settings = settings;
        UnregisterAll();
        var problems = new Dictionary<PowerMode, string>();

        if (settings.Enabled && !_suspended)
        {
            foreach (var mode in PowerModes.All)
            {
                var text = settings.For(mode);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (!Hotkey.TryParse(text, out var hotkey, out var error))
                {
                    problems[mode] = error!;
                    continue;
                }

                var id = (int)mode + 1;
                if (RegisterHotKey(Handle, id, (int)hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey))
                {
                    _registered.Add(id);
                }
                else
                {
                    var code = Marshal.GetLastPInvokeError();
                    problems[mode] = code == ErrorHotkeyAlreadyRegistered
                        ? $"{hotkey} is already used by another app."
                        : $"{hotkey} could not be registered (error {code}).";
                }
            }
        }

        if (!_suspended)
        {
            foreach (var problem in problems.Values.Except(Problems.Values))
                Log.Write($"Shortcut problem: {problem}");
            Problems = problems;
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam is var id and >= 1 and <= 3)
            Pressed?.Invoke((PowerMode)(id - 1));
        base.WndProc(ref m);
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered)
            UnregisterHotKey(Handle, id);
        _registered.Clear();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
