using UwPower.Core;
using UwPower.Core.Config;
using UwPower.Core.Scheduling;

namespace UwPower.Tests;

public class DaySetTests
{
    [Theory]
    [InlineData("Daily", DaySet.Daily)]
    [InlineData("weekdays", DaySet.Weekdays)]
    [InlineData("Weekends", DaySet.Weekends)]
    [InlineData("Mon-Fri", DaySet.Weekdays)]
    [InlineData("Sat,Sun", DaySet.Weekends)]
    [InlineData("monday, Wednesday,fri", DaySet.Mon | DaySet.Wed | DaySet.Fri)]
    [InlineData("Fri-Mon", DaySet.Fri | DaySet.Sat | DaySet.Sun | DaySet.Mon)]
    [InlineData("tues-thurs", DaySet.Tue | DaySet.Wed | DaySet.Thu)]
    public void Parses(string text, DaySet expected)
    {
        Assert.True(DaySets.TryParse(text, out var days, out var error), error);
        Assert.Equal(expected, days);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Funday")]
    [InlineData("Mon-Fri-Sat")]
    [InlineData("Monkey")]
    public void RejectsInvalid(string text)
    {
        Assert.False(DaySets.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(DaySet.Daily, "Daily")]
    [InlineData(DaySet.Weekdays, "Weekdays")]
    [InlineData(DaySet.Mon | DaySet.Tue | DaySet.Wed | DaySet.Thu, "Mon-Thu")]
    [InlineData(DaySet.Mon | DaySet.Tue | DaySet.Fri, "Mon,Tue,Fri")]
    [InlineData(DaySet.Mon | DaySet.Wed | DaySet.Thu | DaySet.Fri | DaySet.Sun, "Mon,Wed-Fri,Sun")]
    public void Formats(DaySet days, string expected) => Assert.Equal(expected, DaySets.Format(days));

    [Fact]
    public void MapsDayOfWeek()
    {
        Assert.Equal(DaySet.Mon, DaySets.Of(DayOfWeek.Monday));
        Assert.Equal(DaySet.Sun, DaySets.Of(DayOfWeek.Sunday));
    }
}

public class ScheduleEvaluatorTests
{
    // 2026-10-26 is a Monday.
    private static DateTime Mon(int h, int m = 0) => new(2026, 10, 26, h, m, 0);

    private static ScheduleRule Rule(string days, string from, string to, PowerMode mode = PowerMode.Beast) =>
        new() { Days = days, From = from, To = to, Mode = mode };

    [Fact]
    public void SameDayWindow()
    {
        var rule = Rule("Weekdays", "09:00", "17:00");
        Assert.False(ScheduleEvaluator.TryGetActiveStart(rule, Mon(8, 59), out _));
        Assert.True(ScheduleEvaluator.TryGetActiveStart(rule, Mon(9), out var start));
        Assert.Equal(Mon(9), start);
        Assert.True(ScheduleEvaluator.TryGetActiveStart(rule, Mon(16, 59), out _));
        Assert.False(ScheduleEvaluator.TryGetActiveStart(rule, Mon(17), out _));
        Assert.False(ScheduleEvaluator.TryGetActiveStart(rule, Mon(10).AddDays(-1), out _)); // Sunday
    }

    [Fact]
    public void OvernightWindowBelongsToTheDayItStarted()
    {
        var rule = Rule("Sun", "22:00", "06:00");
        // Monday 03:00 is inside Sunday night's window.
        Assert.True(ScheduleEvaluator.TryGetActiveStart(rule, Mon(3), out var start));
        Assert.Equal(Mon(22).AddDays(-1), start);
        // Monday 22:00 is not, because Monday isn't listed.
        Assert.False(ScheduleEvaluator.TryGetActiveStart(rule, Mon(22), out _));
    }

    [Fact]
    public void EqualTimesMeanFullDay()
    {
        var rule = Rule("Mon", "00:00", "00:00");
        Assert.True(ScheduleEvaluator.TryGetActiveStart(rule, Mon(0), out _));
        Assert.True(ScheduleEvaluator.TryGetActiveStart(rule, Mon(23, 59), out _));
        Assert.False(ScheduleEvaluator.TryGetActiveStart(rule, Mon(0).AddDays(1), out _));
    }

    [Fact]
    public void LastMatchingRuleWinsAndDisabledRulesAreSkipped()
    {
        var all = Rule("Daily", "00:00", "00:00", PowerMode.Balanced);
        var work = Rule("Weekdays", "09:00", "17:00", PowerMode.Beast);
        var off = Rule("Daily", "10:00", "11:00", PowerMode.Enthusiast);
        off.Enabled = false;

        Assert.Equal(PowerMode.Beast, ScheduleEvaluator.FindActive([all, work, off], Mon(10, 30))!.Rule.Mode);
        Assert.Equal(PowerMode.Balanced, ScheduleEvaluator.FindActive([all, work, off], Mon(18))!.Rule.Mode);
        Assert.Null(ScheduleEvaluator.FindActive([work], Mon(18)));
    }

    [Theory]
    [InlineData("Daily", "9:00", "17:30", null)]
    [InlineData("Daily", "25:00", "17:00", "Start time")]
    [InlineData("Daily", "09:00", "5pm", "End time")]
    [InlineData("Someday", "09:00", "17:00", "not a day")]
    public void Validates(string days, string from, string to, string? errorFragment)
    {
        var error = ScheduleEvaluator.Validate(Rule(days, from, to));
        if (errorFragment is null)
            Assert.Null(error);
        else
            Assert.Contains(errorFragment, error);
    }
}
