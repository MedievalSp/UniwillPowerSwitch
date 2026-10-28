using System.Globalization;
using System.Text.Json.Nodes;
using UwPower.Core;
using UwPower.Core.Config;
using UwPower.Core.ControlCenter;
using UwPower.Core.Idle;
using UwPower.Core.Ipc;
using UwPower.Core.Scheduling;

namespace UwPower.Cli;

internal static class Program
{
    private const string Usage = """
        uwpower - switch Uniwill/Eluktronics power modes (Balanced, Enthusiast, Beast)

        Modes:
          uwpower status                       Current mode, CPU/GPU limits, automation state
          uwpower set <mode>                   balanced | enthusiast | beast  (aliases: office, gaming, turbo)
          uwpower auto on|off                  Resume / pause schedules and idle switching

        Idle (switch to a mode while you're away):
          uwpower idle                         Show idle settings
          uwpower idle set [--enabled true|false] [--minutes N] [--mode <mode>]
                           [--power-requests true|false] [--fullscreen true|false]
          uwpower idle check                   Show what currently counts as activity
          uwpower idle ignore add|remove <text>   Ignore power requests containing <text>

        Schedule:
          uwpower schedule                     List rules
          uwpower schedule add --days <days> --from HH:mm --to HH:mm --mode <mode>
                                               days: Daily, Weekdays, Weekends, Mon-Fri, Sat,Sun ...
                                               --to earlier than --from runs past midnight
          uwpower schedule remove|enable|disable <id>
          uwpower schedule clear

        Keyboard shortcuts (handled by the tray app):
          uwpower hotkeys                      Show shortcuts
          uwpower hotkeys set [--enabled on|off] [--balanced <keys>] [--enthusiast <keys>] [--beast <keys>]
                                               keys: e.g. Ctrl+Alt+Shift+1, Win+Alt+F9, or "none"

        Startup:
          uwpower autostart enable|disable|status   Start the tray app (elevated) at logon

        Settings live in %APPDATA%\UwPower\config.json; the log is %APPDATA%\UwPower\uwpower.log.
        """;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run 'uwpower help' for usage.");
            return 2;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        var rest = args.Skip(1).ToArray();
        switch (command)
        {
            case "status":
                return await StatusAsync();
            case "set":
                return await SetAsync(Arg(rest, 0, "mode"));
            case "auto":
                return await AutoAsync(Arg(rest, 0, "on|off"));
            case "idle":
                return await IdleAsync(rest);
            case "schedule":
                return await ScheduleAsync(rest);
            case "hotkeys" or "shortcuts":
                return await HotkeysAsync(rest);
            case "autostart":
                return Autostart(rest);
            case "help" or "-h" or "--help" or "/?":
                Console.WriteLine(Usage);
                return 0;
            default:
                throw new UsageException($"Unknown command '{args[0]}'.");
        }
    }

    // ---- modes ---------------------------------------------------------------------------

    private static async Task<int> StatusAsync()
    {
        var config = ConfigStore.Load();
        var tray = await TrayPipe.TrySendAsync(new JsonObject { ["cmd"] = "status" });
        if (tray is not null)
        {
            if (tray["connected"]?.GetValue<bool>() != true)
            {
                Console.WriteLine($"Tray app running, but not connected: {tray["error"]}");
                return 1;
            }
            Console.WriteLine($"Mode:       {tray["details"] ?? tray["mode"]}");
            Console.WriteLine($"Automation: {(config.AutomationEnabled ? "on" : "paused")} - {tray["reason"]}");
            if (tray["inputIdleSeconds"] is { } idleSeconds)
            {
                var blockers = tray["idleBlockers"]?.AsArray().Select(b => (string?)b).ToList() ?? [];
                Console.WriteLine($"Idle:       {(tray["idle"]?.GetValue<bool>() == true ? "yes" : "no")} " +
                                  $"(no input for {FormatDuration(TimeSpan.FromSeconds((int)idleSeconds!))})" +
                                  (blockers.Count > 0 ? $"; kept active by: {string.Join("; ", blockers)}" : ""));
            }
            Console.WriteLine($"Tray app:   running{(tray["elevated"]?.GetValue<bool>() == true ? " (elevated)" : " (not elevated - videos/games are not detected)")}");
            return 0;
        }

        await using var client = await ControlCenterClient.ConnectAsync(config.Broker);
        var status = await client.GetStatusAsync();
        Console.WriteLine($"Mode:       {status.Describe()}");
        Console.WriteLine($"Automation: tray app not running (schedules and idle switching need it)");
        return 0;
    }

    private static async Task<int> SetAsync(string modeText)
    {
        var mode = ParseMode(modeText);
        var tray = await TrayPipe.TrySendAsync(new JsonObject { ["cmd"] = "set", ["mode"] = mode.ToString() });
        if (tray is not null)
        {
            if (tray["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException((string?)tray["error"] ?? "The tray app could not switch modes.");
            Console.WriteLine($"Switched: {tray["details"] ?? mode.ToString()}");
            return 0;
        }

        var config = ConfigStore.Load();
        await using var client = await ControlCenterClient.ConnectAsync(config.Broker);
        await client.SetModeAsync(mode);
        ConfigStore.Update(c => c.DefaultMode = mode);
        Console.WriteLine($"Switched: {(await client.GetStatusAsync()).Describe()}");
        return 0;
    }

    private static async Task<int> AutoAsync(string value)
    {
        var enabled = ParseBool(value, "auto");
        ConfigStore.Update(c => c.AutomationEnabled = enabled);
        await NotifyTrayAsync();
        Console.WriteLine(enabled ? "Automation on: schedules and idle switching are active." : "Automation paused.");
        return 0;
    }

    // ---- idle ----------------------------------------------------------------------------

    private static async Task<int> IdleAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
        switch (sub)
        {
            case "show":
                PrintIdle(ConfigStore.Load().Idle);
                return 0;

            case "set":
                var options = ParseOptions(args.Skip(1), "enabled", "minutes", "mode", "power-requests", "fullscreen");
                if (options.Count == 0)
                    throw new UsageException("Nothing to set. Example: uwpower idle set --minutes 15 --mode balanced");
                var updated = ConfigStore.Update(c =>
                {
                    if (options.TryGetValue("enabled", out var v))
                        c.Idle.Enabled = ParseBool(v, "--enabled");
                    if (options.TryGetValue("minutes", out v))
                        c.Idle.Minutes = ParseMinutes(v);
                    if (options.TryGetValue("mode", out v))
                        c.Idle.Mode = ParseMode(v);
                    if (options.TryGetValue("power-requests", out v))
                        c.Idle.RespectPowerRequests = ParseBool(v, "--power-requests");
                    if (options.TryGetValue("fullscreen", out v))
                        c.Idle.FullscreenCountsAsActive = ParseBool(v, "--fullscreen");
                });
                await NotifyTrayAsync();
                PrintIdle(updated.Idle);
                return 0;

            case "check":
                return IdleCheck();

            case "ignore":
                var action = Arg(args, 1, "add|remove").ToLowerInvariant();
                var text = string.Join(' ', args.Skip(2));
                if (text.Length == 0)
                    throw new UsageException("Give the text to match, e.g. uwpower idle ignore add \"Realtek\"");
                var result = ConfigStore.Update(c =>
                {
                    if (action == "add" && !c.Idle.IgnoredRequests.Contains(text, StringComparer.OrdinalIgnoreCase))
                        c.Idle.IgnoredRequests.Add(text);
                    else if (action == "remove")
                        c.Idle.IgnoredRequests.RemoveAll(t => t.Equals(text, StringComparison.OrdinalIgnoreCase));
                    else if (action != "add")
                        throw new UsageException("Use 'idle ignore add <text>' or 'idle ignore remove <text>'.");
                });
                await NotifyTrayAsync();
                PrintIdle(result.Idle);
                return 0;

            default:
                throw new UsageException($"Unknown idle command '{args[0]}'.");
        }
    }

    private static int IdleCheck()
    {
        var settings = ConfigStore.Load().Idle;
        var snapshot = new IdleDetector().Evaluate(settings, checkBlockersAlways: true);
        Console.WriteLine($"No keyboard/mouse input for {FormatDuration(snapshot.InputIdle)} (threshold {FormatDuration(snapshot.Threshold)}).");
        if (snapshot.Blockers.Count == 0)
            Console.WriteLine("Nothing is keeping the PC active.");
        else
        {
            Console.WriteLine("Counted as activity (idle switching waits while these are present):");
            foreach (var blocker in snapshot.Blockers)
                Console.WriteLine($"  - {blocker}");
        }
        if (snapshot.Note is not null)
            Console.WriteLine($"Note: {snapshot.Note}");
        Console.WriteLine(snapshot.IsIdle ? "Result: idle." : "Result: not idle.");
        return 0;
    }

    private static void PrintIdle(IdleSettings idle)
    {
        Console.WriteLine($"Idle switching:   {(idle.Enabled ? "on" : "off")}");
        Console.WriteLine($"After:            {idle.Minutes.ToString(CultureInfo.InvariantCulture)} min without input");
        Console.WriteLine($"Switch to:        {idle.Mode}");
        Console.WriteLine($"Power requests:   {(idle.RespectPowerRequests ? "videos, games, calls and audio keep the PC active" : "ignored")}");
        Console.WriteLine($"Fullscreen apps:  {(idle.FullscreenCountsAsActive ? "keep the PC active" : "ignored")}");
        if (idle.IgnoredRequests.Count > 0)
            Console.WriteLine($"Ignored requests: {string.Join(", ", idle.IgnoredRequests)}");
    }

    // ---- schedule ------------------------------------------------------------------------

    private static async Task<int> ScheduleAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        AppConfig config;
        switch (sub)
        {
            case "list":
                PrintSchedule(ConfigStore.Load());
                return 0;

            case "add":
                var options = ParseOptions(args.Skip(1), "days", "from", "to", "mode");
                foreach (var required in new[] { "from", "to", "mode" })
                {
                    if (!options.ContainsKey(required))
                        throw new UsageException($"Missing --{required}. Example: uwpower schedule add --days Weekdays --from 09:00 --to 18:00 --mode enthusiast");
                }
                var rule = new ScheduleRule
                {
                    Days = options.GetValueOrDefault("days", "Daily"),
                    From = options["from"],
                    To = options["to"],
                    Mode = ParseMode(options["mode"]),
                };
                if (ScheduleEvaluator.Validate(rule) is { } error)
                    throw new UsageException(error);
                DaySets.TryParse(rule.Days, out var days, out _);
                rule.Days = DaySets.Format(days);
                config = ConfigStore.Update(c => c.Schedule.Add(rule));
                Console.WriteLine($"Added rule {rule.Id}.");
                break;

            case "remove" or "enable" or "disable":
                var id = Arg(args, 1, "rule id");
                config = ConfigStore.Update(c =>
                {
                    var match = c.Schedule.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        ?? throw new UsageException($"No schedule rule with id '{id}'. Run 'uwpower schedule' to list them.");
                    if (sub == "remove")
                        c.Schedule.Remove(match);
                    else
                        match.Enabled = sub == "enable";
                });
                break;

            case "clear":
                config = ConfigStore.Update(c => c.Schedule.Clear());
                break;

            default:
                throw new UsageException($"Unknown schedule command '{args[0]}'.");
        }

        await NotifyTrayAsync();
        PrintSchedule(config);
        return 0;
    }

    private static void PrintSchedule(AppConfig config)
    {
        if (config.Schedule.Count == 0)
        {
            Console.WriteLine("No schedule rules.");
            return;
        }
        var active = ScheduleEvaluator.FindActive(config.Schedule, DateTime.Now);
        Console.WriteLine("ID      On   Days                  From   To     Mode");
        foreach (var r in config.Schedule)
        {
            var marker = active?.Rule.Id == r.Id ? "  <- active now" : "";
            Console.WriteLine($"{r.Id,-7} {(r.Enabled ? "yes" : "no"),-4} {r.Days,-21} {r.From,-6} {r.To,-6} {r.Mode}{marker}");
        }
        Console.WriteLine("When rules overlap, the one lower in the list wins.");
    }

    // ---- hotkeys -------------------------------------------------------------------------

    private static async Task<int> HotkeysAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
        if (sub == "show")
        {
            PrintHotkeys(ConfigStore.Load().Hotkeys);
            return 0;
        }
        if (sub != "set")
            throw new UsageException($"Unknown hotkeys command '{args[0]}'.");

        var options = ParseOptions(args.Skip(1), "enabled", "balanced", "enthusiast", "beast");
        if (options.Count == 0)
            throw new UsageException("Nothing to set. Example: uwpower hotkeys set --beast Ctrl+Alt+Shift+F12");

        var updated = ConfigStore.Update(c =>
        {
            if (options.TryGetValue("enabled", out var enabled))
                c.Hotkeys.Enabled = ParseBool(enabled, "--enabled");
            foreach (var mode in PowerModes.All)
            {
                if (!options.TryGetValue(mode.ToString(), out var keys))
                    continue;
                if (keys.Equals("none", StringComparison.OrdinalIgnoreCase) || keys.Length == 0)
                    c.Hotkeys.Set(mode, null);
                else if (Hotkey.TryParse(keys, out var hotkey, out var error))
                    c.Hotkeys.Set(mode, hotkey.ToString());
                else
                    throw new UsageException(error!);
            }
            if (c.Hotkeys.Validate() is { } problem)
                throw new UsageException(problem);
        });
        await NotifyTrayAsync();
        PrintHotkeys(updated.Hotkeys);
        return 0;
    }

    private static void PrintHotkeys(HotkeySettings hotkeys)
    {
        Console.WriteLine($"Shortcuts:  {(hotkeys.Enabled ? "on" : "off")}");
        foreach (var mode in PowerModes.All)
            Console.WriteLine($"{mode + ":",-11} {hotkeys.For(mode) ?? "none"}");
    }

    // ---- autostart -----------------------------------------------------------------------

    private static int Autostart(string[] args)
    {
        var sub = Arg(args, 0, "enable|disable|status").ToLowerInvariant();
        switch (sub)
        {
            case "status":
                Console.WriteLine(UwPower.Core.Autostart.IsEnabled()
                    ? $"Enabled (Task Scheduler task \"{UwPower.Core.Autostart.TaskName}\")."
                    : "Disabled.");
                return 0;

            case "enable" or "disable":
                if (!PowerRequests.CanQuery)
                {
                    Console.WriteLine("Asking for administrator permission to update the logon task...");
                    var code = UwPower.Core.Autostart.RunElevated(Environment.ProcessPath!, $"autostart {sub}");
                    Console.WriteLine(code == 0 ? $"Autostart {sub}d." : "Failed; see the elevated window's output.");
                    return code;
                }
                if (sub == "enable")
                {
                    var trayExe = Path.Combine(AppContext.BaseDirectory, "UwPowerTray.exe");
                    if (!File.Exists(trayExe))
                        throw new FileNotFoundException($"UwPowerTray.exe not found next to uwpower.exe ({trayExe}).");
                    UwPower.Core.Autostart.Enable(trayExe);
                }
                else
                {
                    UwPower.Core.Autostart.Disable();
                }
                Console.WriteLine($"Autostart {sub}d.");
                return 0;

            default:
                throw new UsageException($"Unknown autostart command '{args[0]}'.");
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    private static async Task NotifyTrayAsync()
    {
        try
        {
            await TrayPipe.TrySendAsync(new JsonObject { ["cmd"] = "reload" });
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            Console.Error.WriteLine("Note: saved, but the tray app did not acknowledge; it will pick up changes on restart.");
        }
    }

    private static string Arg(string[] args, int index, string name) =>
        args.Length > index ? args[index] : throw new UsageException($"Missing argument: {name}.");

    private static PowerMode ParseMode(string text) =>
        PowerModes.TryParse(text, out var mode)
            ? mode
            : throw new UsageException($"Unknown mode '{text}'. Use balanced, enthusiast or beast.");

    private static bool ParseBool(string text, string name) => text.ToLowerInvariant() switch
    {
        "true" or "on" or "yes" or "1" => true,
        "false" or "off" or "no" or "0" => false,
        _ => throw new UsageException($"{name} expects on/off (or true/false), got '{text}'."),
    };

    private static double ParseMinutes(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0 && m <= 24 * 60
            ? m
            : throw new UsageException($"--minutes expects a number of minutes between 0 and 1440, got '{text}'.");

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args, params string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var name = e.Current.TrimStart('-');
            if (!e.Current.StartsWith("--") || !allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new UsageException($"Unexpected option '{e.Current}'. Allowed: {string.Join(", ", allowed.Select(a => "--" + a))}.");
            if (!e.MoveNext())
                throw new UsageException($"Option --{name} needs a value.");
            result[name] = e.Current;
        }
        return result;
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
        : $"{t.Seconds}s";

    private sealed class UsageException(string message) : Exception(message);
}
