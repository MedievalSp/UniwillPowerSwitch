using System.Globalization;
using UwPower.Core.Config;

namespace UwPower.Core.Scheduling;

/// <summary>A schedule rule that is active right now, and when this occurrence of it started.</summary>
public sealed record ActiveWindow(ScheduleRule Rule, DateTime Start)
{
    /// <summary>Identifies this particular occurrence, so a manual override can last until it ends.</summary>
    public string Key => $"{Rule.Id}@{Start:yyyyMMddHHmm}";
}

public static class ScheduleEvaluator
{
    /// <summary>Returns the active window, or null. When rules overlap, the last one in the list wins.</summary>
    public static ActiveWindow? FindActive(IEnumerable<ScheduleRule> rules, DateTime now)
    {
        ActiveWindow? active = null;
        foreach (var rule in rules)
        {
            if (rule.Enabled && TryGetActiveStart(rule, now, out var start))
                active = new ActiveWindow(rule, start);
        }
        return active;
    }

    /// <summary>Returns an error message, or null when the rule is valid.</summary>
    public static string? Validate(ScheduleRule rule)
    {
        if (!DaySets.TryParse(rule.Days, out _, out var dayError))
            return dayError;
        if (!TryParseTime(rule.From, out _))
            return $"Start time '{rule.From}' must be 24-hour HH:mm, e.g. 09:00.";
        if (!TryParseTime(rule.To, out _))
            return $"End time '{rule.To}' must be 24-hour HH:mm, e.g. 18:30.";
        return null;
    }

    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        if (TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t))
        {
            time = t.ToTimeSpan();
            return true;
        }
        time = default;
        return false;
    }

    /// <summary>
    /// A rule starts at From on each listed day and lasts until To. If To is not after From, the rule
    /// runs past midnight (22:00-06:00), and From == To means a full 24 hours.
    /// </summary>
    public static bool TryGetActiveStart(ScheduleRule rule, DateTime now, out DateTime start)
    {
        start = default;
        if (!DaySets.TryParse(rule.Days, out var days, out _) ||
            !TryParseTime(rule.From, out var from) || !TryParseTime(rule.To, out var to))
            return false;

        var duration = to > from ? to - from : TimeSpan.FromDays(1) - (from - to);

        // Only today's or yesterday's occurrence can cover "now", since rules last at most 24 hours.
        foreach (var day in new[] { now.Date, now.Date.AddDays(-1) })
        {
            var candidate = day + from;
            if (days.HasFlag(DaySets.Of(day.DayOfWeek)) && now >= candidate && now < candidate + duration)
            {
                start = candidate;
                return true;
            }
        }
        return false;
    }
}
