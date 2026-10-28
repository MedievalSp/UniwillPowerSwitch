using UwPower.Core.Config;
using UwPower.Core.Idle;

namespace UwPower.Tests;

public class PowerRequestParserTests
{
    private const string Sample = """
        DISPLAY:
        [PROCESS] \Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe
        Video Wake Lock

        SYSTEM:
        [DRIVER] Realtek High Definition Audio(SST) (HDAUDIO\FUNC_01&VEN_10EC&DEV_0295&SUBSYS_1D051234&REV_1000\5&2b3e1d6&0&0001)
        An audio stream is currently in use.
        [PROCESS] \Device\HarddiskVolume3\Program Files\Steam\steam.exe

        AWAYMODE:
        None.

        EXECUTION:
        [PROCESS] \Device\HarddiskVolume3\Windows\SystemApps\Shell.exe
        Background task

        PERFBOOST:
        None.

        ACTIVELOCKSCREEN:
        None.
        """;

    [Fact]
    public void ParsesEntriesWithAndWithoutReasons()
    {
        var requests = PowerRequests.Parse(Sample.Replace("\n", "\r\n"));
        Assert.Equal(4, requests.Count);

        Assert.Equal(new PowerRequest("DISPLAY", "PROCESS",
            @"\Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe", "Video Wake Lock"), requests[0]);
        Assert.Equal("chrome.exe", requests[0].ShortName);

        Assert.Equal("SYSTEM", requests[1].Category);
        Assert.Equal("DRIVER", requests[1].Kind);
        Assert.Equal("An audio stream is currently in use.", requests[1].Reason);

        Assert.Equal("steam.exe", requests[2].ShortName);
        Assert.Null(requests[2].Reason);

        Assert.Equal("EXECUTION", requests[3].Category);
        Assert.False(requests[3].BlocksIdle);
        Assert.True(requests[0].BlocksIdle);
    }

    [Fact]
    public void EmptyOutputHasNoRequests()
    {
        Assert.Empty(PowerRequests.Parse("DISPLAY:\nNone.\n\nSYSTEM:\nNone.\n"));
    }
}

public class IdleDetectorTests
{
    private static readonly PowerRequest Video = new("DISPLAY", "PROCESS", @"C:\x\vlc.exe", "Playing video");
    private static readonly PowerRequest Background = new("EXECUTION", "PROCESS", @"C:\x\bg.exe", null);

    private static IdleDetector Detector(TimeSpan inputIdle, string? fullscreen = null, bool elevated = true,
        params PowerRequest[] requests) =>
        new(() => inputIdle, () => fullscreen, () => elevated, () => requests);

    private static readonly IdleSettings Settings = new() { Minutes = 10 };

    [Fact]
    public void ActiveInputIsNeverIdle()
    {
        var s = Detector(TimeSpan.FromMinutes(2)).Evaluate(Settings);
        Assert.False(s.IsIdle);
        Assert.Empty(s.Blockers);
    }

    [Fact]
    public void IdleWhenNoInputAndNothingHoldsTheSystem()
    {
        Assert.True(Detector(TimeSpan.FromMinutes(11), requests: Background).Evaluate(Settings).IsIdle);
    }

    [Fact]
    public void VideoPowerRequestKeepsItActive()
    {
        var s = Detector(TimeSpan.FromMinutes(30), requests: Video).Evaluate(Settings);
        Assert.False(s.IsIdle);
        Assert.Contains("vlc.exe", Assert.Single(s.Blockers));
    }

    [Fact]
    public void IgnoredRequestsDoNotBlock()
    {
        var settings = new IdleSettings { Minutes = 10, IgnoredRequests = ["VLC"] };
        Assert.True(Detector(TimeSpan.FromMinutes(30), requests: Video).Evaluate(settings).IsIdle);
    }

    [Fact]
    public void FullscreenAppKeepsItActiveUnlessDisabled()
    {
        Assert.False(Detector(TimeSpan.FromMinutes(30), fullscreen: "game").Evaluate(Settings).IsIdle);
        var settings = new IdleSettings { Minutes = 10, FullscreenCountsAsActive = false };
        Assert.True(Detector(TimeSpan.FromMinutes(30), fullscreen: "game").Evaluate(settings).IsIdle);
    }

    [Fact]
    public void WithoutElevationFallsBackToInputOnlyWithNote()
    {
        var s = Detector(TimeSpan.FromMinutes(30), elevated: false, requests: Video).Evaluate(Settings);
        Assert.True(s.IsIdle);
        Assert.NotNull(s.Note);
    }
}
