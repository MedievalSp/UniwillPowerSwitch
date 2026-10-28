using System.Diagnostics;
using System.Reflection;
using UwPower.Core;
using UwPower.Core.Automation;
using UwPower.Core.Config;
using UwPower.Core.Ipc;

namespace UwPower.Tray;

/// <summary>The notification-area icon and its menu. Owns the <see cref="PowerController"/>.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly PowerController _controller = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SynchronizationContext _ui;
    private readonly ModeIcons _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _automationItem;
    private readonly Dictionary<PowerMode, ToolStripMenuItem> _modeItems = [];
    private readonly GlobalHotkeys _hotkeys = new();
    private readonly ModeOsd _osd = new();
    private SettingsForm? _settings;
    private bool _exiting;

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var mode in PowerModes.All)
        {
            var item = new ToolStripMenuItem(mode.ToString(), _icons.For(mode).ToBitmap(), async (_, _) => await SetModeAsync(mode));
            _modeItems[mode] = item;
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        _automationItem = new ToolStripMenuItem("Automatic switching (schedule && idle)", null, (_, _) => ToggleAutomation());
        menu.Items.Add(_automationItem);
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _icons.For(null),
            Text = "Uniwill Power Switch: connecting...",
            Visible = true,
        };
        _notifyIcon.MouseUp += OnIconClick;
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();

        _controller.StateChanged += () => _ui.Post(_ => RefreshUi(), null);
        _controller.ConfigReloaded += () => _ui.Post(_ => ApplyHotkeys(), null);
        _hotkeys.Pressed += async mode => await OnHotkeyAsync(mode);
        ApplyHotkeys();
        _controller.Start();
        _ = TrayPipe.ServeAsync(_controller, _cts.Token);
        Log.Write($"Tray app started (elevated: {Core.Idle.PowerRequests.CanQuery}).");
        RefreshUi();
    }

    public PowerController Controller => _controller;

    public ModeIcons Icons => _icons;

    public GlobalHotkeys Hotkeys => _hotkeys;

    protected override void ExitThreadCore()
    {
        // Refreshes queued by the controller may still run after this; they must not touch disposed UI.
        _exiting = true;
        _cts.Cancel();
        _notifyIcon.Visible = false;
        _settings?.Close();
        _hotkeys.Dispose();
        _osd.Dispose();
        // Give the controller a moment to close the broker connection cleanly.
        Task.Run(async () => await _controller.DisposeAsync()).Wait(TimeSpan.FromSeconds(2));
        _notifyIcon.Dispose();
        _icons.Dispose();
        Log.Write("Tray app exited.");
        base.ExitThreadCore();
    }

    private void OnIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        // Left click opens the same menu as right click.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(_notifyIcon, null);
    }

    private async Task SetModeAsync(PowerMode mode)
    {
        try
        {
            await _controller.SetModeManuallyAsync(mode);
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(5000, "Could not switch mode", ex.Message, ToolTipIcon.Warning);
        }
    }

    private void ApplyHotkeys()
    {
        if (_exiting)
            return;
        _hotkeys.Apply(_controller.Config.Hotkeys);
        _settings?.RefreshState();
    }

    private async Task OnHotkeyAsync(PowerMode mode)
    {
        _osd.ShowMessage(mode, mode.ToString());
        try
        {
            await _controller.SetModeManuallyAsync(mode);
        }
        catch (Exception ex)
        {
            _osd.ShowMessage(null, "Could not switch mode");
            Log.Write($"Shortcut for {mode} failed: {ex.Message}");
        }
    }

    private void ToggleAutomation()
    {
        var enabled = !_controller.Config.AutomationEnabled;
        ConfigStore.Update(c => c.AutomationEnabled = enabled);
        _controller.ReloadConfig();
        RefreshUi();
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(this);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
    }

    private static void OpenLog()
    {
        if (File.Exists(Log.FilePath))
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    private void RefreshUi()
    {
        if (_exiting)
            return;
        var mode = _controller.CurrentMode;
        var connected = _controller.Connected;
        var automation = _controller.Config.AutomationEnabled;

        _notifyIcon.Icon = _icons.For(connected ? mode : null);
        foreach (var (m, item) in _modeItems)
        {
            item.Checked = connected && m == mode;
            item.Enabled = connected;
        }
        _automationItem.Checked = automation;

        string status;
        if (!connected)
            status = _controller.LastError ?? "Connecting to Control Center service...";
        else if (!automation)
            status = $"{mode} (automation paused)";
        else
            status = $"{mode} - {_controller.LastDecision?.Reason ?? "starting"}";
        _statusItem.Text = Truncate(status, 80);

        // NotifyIcon tooltips are limited to 127 characters.
        _notifyIcon.Text = Truncate($"Power mode: {(connected ? mode?.ToString() ?? "unknown" : "not connected")}", 127);
        _settings?.RefreshState();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
