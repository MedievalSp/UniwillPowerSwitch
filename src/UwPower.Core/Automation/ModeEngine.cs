using UwPower.Core.Config;
using UwPower.Core.Scheduling;

namespace UwPower.Core.Automation;

public sealed record ModeDecision(PowerMode? Mode, string Reason);

/// <summary>
/// Decides which mode the laptop should be in. Pure logic, no I/O. Priority:
/// <list type="number">
/// <item>Automation paused: nothing is decided, only manual changes happen.</item>
/// <item>Idle (and idle switching enabled): the idle mode.</item>
/// <item>Inside a schedule window: that rule's mode, unless you picked a mode manually during
/// this window - then your pick holds until the window ends.</item>
/// <item>Otherwise: the default mode, which is the last mode you picked manually outside a window.</item>
/// </list>
/// </summary>
public sealed class ModeEngine(AppConfig config)
{
    private AppConfig _config = config;
    private (PowerMode Mode, string WindowKey)? _override;
    private bool _idleSuppressed;

    public AppConfig Config => _config;

    public void UpdateConfig(AppConfig config) => _config = config;

    public ModeDecision Decide(DateTime now, bool idle)
    {
        if (!idle)
            _idleSuppressed = false;

        if (!_config.AutomationEnabled)
            return new ModeDecision(null, "automation paused");

        if (idle && _config.Idle.Enabled && !_idleSuppressed)
            return new ModeDecision(_config.Idle.Mode, "idle");

        var window = ScheduleEvaluator.FindActive(_config.Schedule, now);
        if (window is not null)
        {
            if (_override is { } o && o.WindowKey == window.Key)
                return new ModeDecision(o.Mode, $"manual choice until schedule \"{window.Rule}\" ends");
            return new ModeDecision(window.Rule.Mode, $"schedule \"{window.Rule}\"");
        }

        _override = null;
        return _config.DefaultMode is { } mode
            ? new ModeDecision(mode, "default mode")
            : new ModeDecision(null, "no default mode yet");
    }

    /// <summary>
    /// Records a mode you picked (from the tray, CLI, Control Center or the Fn hotkey).
    /// Returns true when <see cref="AppConfig.DefaultMode"/> changed and should be saved.
    /// </summary>
    public bool OnManualSelection(PowerMode mode, DateTime now, bool idle)
    {
        // A mode picked while "idle" (e.g. from a remote shell) should stick until you're back.
        if (idle)
            _idleSuppressed = true;

        var window = _config.AutomationEnabled ? ScheduleEvaluator.FindActive(_config.Schedule, now) : null;
        if (window is not null)
        {
            _override = (mode, window.Key);
            return false;
        }

        _override = null;
        if (_config.DefaultMode == mode)
            return false;
        _config.DefaultMode = mode;
        return true;
    }
}
