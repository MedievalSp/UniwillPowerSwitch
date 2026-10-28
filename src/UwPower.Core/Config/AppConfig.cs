namespace UwPower.Core.Config;

public sealed class AppConfig
{
    /// <summary>Mode to use when no schedule rule is active. Updated whenever you pick a mode manually.</summary>
    public PowerMode? DefaultMode { get; set; }

    /// <summary>When false, schedules and idle switching are paused; only manual changes happen.</summary>
    public bool AutomationEnabled { get; set; } = true;

    public IdleSettings Idle { get; set; } = new();

    /// <summary>Schedule rules. When several are active at once, the one lower in the list wins.</summary>
    public List<ScheduleRule> Schedule { get; set; } = [];

    public HotkeySettings Hotkeys { get; set; } = new();

    public BrokerSettings Broker { get; set; } = new();
}

/// <summary>Global keyboard shortcuts handled by the tray app. Null or empty means no shortcut.</summary>
public sealed class HotkeySettings
{
    public bool Enabled { get; set; } = true;

    public string? Balanced { get; set; } = "Ctrl+Alt+Shift+1";

    public string? Enthusiast { get; set; } = "Ctrl+Alt+Shift+2";

    public string? Beast { get; set; } = "Ctrl+Alt+Shift+3";

    public string? For(PowerMode mode) => mode switch
    {
        PowerMode.Balanced => Balanced,
        PowerMode.Enthusiast => Enthusiast,
        _ => Beast,
    };

    public void Set(PowerMode mode, string? shortcut)
    {
        switch (mode)
        {
            case PowerMode.Balanced:
                Balanced = shortcut;
                break;
            case PowerMode.Enthusiast:
                Enthusiast = shortcut;
                break;
            default:
                Beast = shortcut;
                break;
        }
    }

    /// <summary>Returns an error message, or null when every shortcut is valid and unique.</summary>
    public string? Validate()
    {
        var seen = new Dictionary<Hotkey, PowerMode>();
        foreach (var mode in PowerModes.All)
        {
            if (string.IsNullOrWhiteSpace(For(mode)))
                continue;
            if (!Hotkey.TryParse(For(mode), out var hotkey, out var error))
                return $"{mode}: {error}";
            if (seen.TryGetValue(hotkey, out var other))
                return $"{mode} and {other} use the same shortcut ({hotkey}).";
            seen[hotkey] = mode;
        }
        return null;
    }
}

public sealed class IdleSettings
{
    public bool Enabled { get; set; } = true;

    public double Minutes { get; set; } = 10;

    public PowerMode Mode { get; set; } = PowerMode.Balanced;

    /// <summary>
    /// Don't treat the PC as idle while an app holds a Windows power request (video playback,
    /// games, calls, audio) - the same signal Windows uses to decide whether it may sleep.
    /// Reading other apps' requests requires the app to run elevated.
    /// </summary>
    public bool RespectPowerRequests { get; set; } = true;

    /// <summary>Don't treat the PC as idle while a fullscreen app or game is in the foreground.</summary>
    public bool FullscreenCountsAsActive { get; set; } = true;

    /// <summary>Power requests whose name or reason contains any of these strings are ignored.</summary>
    public List<string> IgnoredRequests { get; set; } = [];
}

public sealed class ScheduleRule
{
    public string Id { get; set; } = NewId();

    public bool Enabled { get; set; } = true;

    /// <summary>e.g. "Daily", "Weekdays", "Weekends", "Mon-Fri", "Sat,Sun".</summary>
    public string Days { get; set; } = "Daily";

    /// <summary>Start time, 24h "HH:mm".</summary>
    public string From { get; set; } = "09:00";

    /// <summary>End time, 24h "HH:mm". Earlier than <see cref="From"/> means it runs past midnight.</summary>
    public string To { get; set; } = "17:00";

    public PowerMode Mode { get; set; } = PowerMode.Balanced;

    public static string NewId() => Guid.NewGuid().ToString("N")[..6];

    public override string ToString() => $"{Days} {From}-{To} -> {Mode}";
}

/// <summary>
/// Connection details for Control Center's local MQTT broker (GCUBridge.exe). The broker only accepts
/// the client IDs and logins compiled into GCUBridge; the defaults are the ones its overclocking-tool
/// slot uses, which nothing else on a stock install connects with.
/// </summary>
public sealed class BrokerSettings
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 13688;

    public string ClientId { get; set; } = "OcTool";

    public string Username { get; set; } = "OcToolUser";

    public string Password { get; set; } = "OcToolPwd123";
}
