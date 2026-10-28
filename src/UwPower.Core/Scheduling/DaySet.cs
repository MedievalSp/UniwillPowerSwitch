namespace UwPower.Core.Scheduling;

[Flags]
public enum DaySet
{
    None = 0,
    Mon = 1 << 0,
    Tue = 1 << 1,
    Wed = 1 << 2,
    Thu = 1 << 3,
    Fri = 1 << 4,
    Sat = 1 << 5,
    Sun = 1 << 6,
    Weekdays = Mon | Tue | Wed | Thu | Fri,
    Weekends = Sat | Sun,
    Daily = Weekdays | Weekends,
}

/// <summary>Parses and formats day lists like "Daily", "Weekdays", "Mon-Fri", "Sat,Sun", "Fri-Mon".</summary>
public static class DaySets
{
    private static readonly string[] Names = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public static DaySet Of(DayOfWeek day) => (DaySet)(1 << ((int)day + 6) % 7);

    public static bool TryParse(string? text, out DaySet days, out string? error)
    {
        days = DaySet.None;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "No days given.";
            return false;
        }

        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.ToLowerInvariant();
            switch (token)
            {
                case "daily" or "everyday" or "all":
                    days |= DaySet.Daily;
                    continue;
                case "weekdays":
                    days |= DaySet.Weekdays;
                    continue;
                case "weekends" or "weekend":
                    days |= DaySet.Weekends;
                    continue;
            }

            var range = token.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length > 2 || !TryDayIndex(range[0], out var first) ||
                !TryDayIndex(range[^1], out var last))
            {
                error = $"'{raw}' is not a day or day range (use e.g. Daily, Weekdays, Weekends, Mon-Fri, Sat,Sun).";
                return false;
            }

            for (var i = first; ; i = (i + 1) % 7)
            {
                days |= (DaySet)(1 << i);
                if (i == last)
                    break;
            }
        }
        return true;
    }

    public static string Format(DaySet days)
    {
        switch (days)
        {
            case DaySet.Daily: return "Daily";
            case DaySet.Weekdays: return "Weekdays";
            case DaySet.Weekends: return "Weekends";
            case DaySet.None: return "None";
        }

        var parts = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            if (!days.HasFlag((DaySet)(1 << i)))
                continue;
            var end = i;
            while (end + 1 < 7 && days.HasFlag((DaySet)(1 << (end + 1))))
                end++;
            parts.Add(end - i >= 2 ? $"{Title(i)}-{Title(end)}" : Title(i));
            if (end - i == 1)
                parts.Add(Title(end));
            i = end;
        }
        return string.Join(",", parts);
    }

    private static string Title(int index) => char.ToUpperInvariant(Names[index][0]) + Names[index][1..];

    private static bool TryDayIndex(string token, out int index)
    {
        // Accept "mon", "tues", "monday"... but not arbitrary words that start with a day name.
        index = token.Length >= 3 ? Array.IndexOf(Names, token[..3]) : -1;
        return index >= 0 && FullName(index).StartsWith(token, StringComparison.Ordinal);
    }

    private static string FullName(int index) => index switch
    {
        0 => "monday",
        1 => "tuesday",
        2 => "wednesday",
        3 => "thursday",
        4 => "friday",
        5 => "saturday",
        _ => "sunday",
    };
}
