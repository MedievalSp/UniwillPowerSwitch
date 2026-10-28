using UwPower.Core;
using UwPower.Core.Automation;
using UwPower.Core.Config;

namespace UwPower.Tests;

public class ModeEngineTests
{
    private static readonly DateTime Morning = new(2026, 10, 26, 8, 0, 0); // Monday
    private static readonly DateTime Work = Morning.AddHours(2);
    private static readonly DateTime Evening = Morning.AddHours(10);

    private static ModeEngine Engine(Action<AppConfig>? configure = null)
    {
        var config = new AppConfig
        {
            DefaultMode = PowerMode.Enthusiast,
            Schedule = [new ScheduleRule { Days = "Weekdays", From = "09:00", To = "17:00", Mode = PowerMode.Beast }],
        };
        configure?.Invoke(config);
        return new ModeEngine(config);
    }

    [Fact]
    public void UsesDefaultOutsideScheduleAndRuleInside()
    {
        var engine = Engine();
        Assert.Equal(PowerMode.Enthusiast, engine.Decide(Morning, idle: false).Mode);
        Assert.Equal(PowerMode.Beast, engine.Decide(Work, idle: false).Mode);
        Assert.Equal(PowerMode.Enthusiast, engine.Decide(Evening, idle: false).Mode);
    }

    [Fact]
    public void IdleOverridesEverythingAndReturnsAfterwards()
    {
        var engine = Engine();
        Assert.Equal(PowerMode.Balanced, engine.Decide(Work, idle: true).Mode);
        Assert.Equal(PowerMode.Beast, engine.Decide(Work, idle: false).Mode);
    }

    [Fact]
    public void IdleSwitchingCanBeDisabled()
    {
        var engine = Engine(c => c.Idle.Enabled = false);
        Assert.Equal(PowerMode.Beast, engine.Decide(Work, idle: true).Mode);
    }

    [Fact]
    public void ManualChoiceDuringRuleLastsUntilRuleEnds()
    {
        var engine = Engine();
        Assert.False(engine.OnManualSelection(PowerMode.Balanced, Work, idle: false));
        Assert.Equal(PowerMode.Balanced, engine.Decide(Work.AddHours(1), idle: false).Mode);
        // After the window, back to the default (which the manual choice did not change).
        Assert.Equal(PowerMode.Enthusiast, engine.Decide(Evening, idle: false).Mode);
        // Next day's window starts fresh.
        Assert.Equal(PowerMode.Beast, engine.Decide(Work.AddDays(1), idle: false).Mode);
    }

    [Fact]
    public void ManualChoiceOutsideRuleBecomesDefault()
    {
        var engine = Engine();
        Assert.True(engine.OnManualSelection(PowerMode.Beast, Evening, idle: false));
        Assert.Equal(PowerMode.Beast, engine.Config.DefaultMode);
        Assert.False(engine.OnManualSelection(PowerMode.Beast, Evening, idle: false)); // unchanged
    }

    [Fact]
    public void ManualChoiceWhileIdleSticksUntilUserReturns()
    {
        var engine = Engine();
        Assert.Equal(PowerMode.Balanced, engine.Decide(Evening, idle: true).Mode);
        engine.OnManualSelection(PowerMode.Beast, Evening, idle: true);
        Assert.Equal(PowerMode.Beast, engine.Decide(Evening, idle: true).Mode);
        Assert.Equal(PowerMode.Beast, engine.Decide(Evening, idle: false).Mode);
        // A later idle period switches again.
        Assert.Equal(PowerMode.Balanced, engine.Decide(Evening.AddHours(1), idle: true).Mode);
    }

    [Fact]
    public void PausedAutomationDecidesNothing()
    {
        var engine = Engine(c => c.AutomationEnabled = false);
        Assert.Null(engine.Decide(Work, idle: true).Mode);
        Assert.True(engine.OnManualSelection(PowerMode.Balanced, Work, idle: false));
        Assert.Equal(PowerMode.Balanced, engine.Config.DefaultMode);
    }

    [Fact]
    public void NoDefaultMeansNoDecision()
    {
        var engine = Engine(c => c.DefaultMode = null);
        Assert.Null(engine.Decide(Evening, idle: false).Mode);
    }
}
