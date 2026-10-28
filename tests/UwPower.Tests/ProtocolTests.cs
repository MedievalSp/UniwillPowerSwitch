using System.Text;
using UwPower.Core;
using UwPower.Core.ControlCenter;
using UwPower.Core.Mqtt;

namespace UwPower.Tests;

public class MqttPacketTests
{
    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x80, 0x01 })]
    [InlineData(16383, new byte[] { 0xFF, 0x7F })]
    [InlineData(16384, new byte[] { 0x80, 0x80, 0x01 })]
    public void EncodesRemainingLength(int length, byte[] expected) =>
        Assert.Equal(expected, MqttPacket.EncodeRemainingLength(length));

    [Fact]
    public void PublishRoundTrips()
    {
        var packet = MqttPacket.Publish("Fan/Control", """{"Action":"GETSTATUS"}""");
        Assert.Equal(0x30, packet[0]);
        Assert.Equal(packet.Length - 2, packet[1]);
        var (topic, payload) = MqttPacket.ParsePublish(0, packet.AsSpan(2));
        Assert.Equal("Fan/Control", topic);
        Assert.Equal("""{"Action":"GETSTATUS"}""", payload);
    }

    [Fact]
    public void ConnectCarriesCredentials()
    {
        var packet = MqttPacket.Connect("client", "user", "pass", 60);
        Assert.Equal(0x10, packet[0]);
        var text = Encoding.ASCII.GetString(packet);
        Assert.Contains("MQTT", text);
        Assert.Contains("client", text);
        Assert.Contains("user", text);
        Assert.Contains("pass", text);
        Assert.Equal(0xC2, packet[9]); // connect flags: username, password, clean session
    }
}

public class StatusParserTests
{
    [Fact]
    public void ParsesTrayStatus()
    {
        Assert.Equal(PowerMode.Beast, StatusParser.ParseTrayStatus("""{"OperatingMode":"2","FanBoostEnable":"0"}"""));
        Assert.Null(StatusParser.ParseTrayStatus("""{"OperatingMode":null}"""));
        Assert.Null(StatusParser.ParseTrayStatus("""{"OperatingMode":"7"}"""));
    }

    [Fact]
    public void ParsesFanStatusFromAmdLaptop()
    {
        // Trimmed from a real Fan/Status message (Eluktronics Prometheus XVII, Beast mode).
        const string json = """
            {"OverClockingSwitch":"1","OperatingMode":"2","ProfileName":"Mode3_Profile3","FAN_TableName":"M3T3",
             "CPU_PL1":"60","CPU_AmdSPL":"60","GPU_ConfigurableTGPSwitch":"1","GPU_ConfigurableTGPTarget":"165",
             "GPU_DynamicBoostSwitch":"0","GPU_DynamicBoost":"15","IsAMDPlatform":true,"PowerMode":"1"}
            """;
        var status = StatusParser.ParseFanStatus(json);
        Assert.Equal(new ControlCenterStatus(PowerMode.Beast, "Mode3_Profile3", 60, 165, null, "M3T3"), status);
        Assert.Equal("Beast, CPU 60 W, GPU 165 W, fan table M3T3", status.Describe());
    }

    [Fact]
    public void ReportsDynamicBoostWhenTgpIsDefault()
    {
        const string json = """
            {"OperatingMode":"0","CPU_AmdSPL":"35","GPU_ConfigurableTGPSwitch":"0","GPU_ConfigurableTGPTarget":"115",
             "GPU_DynamicBoostSwitch":"1","GPU_DynamicBoost":"15","IsAMDPlatform":true}
            """;
        var status = StatusParser.ParseFanStatus(json);
        Assert.Null(status.GpuTgpW);
        Assert.Equal(15, status.GpuDynamicBoostW);
        Assert.Equal(PowerMode.Balanced, status.Mode);
    }

    [Theory]
    [InlineData("balanced", PowerMode.Balanced)]
    [InlineData("Office", PowerMode.Balanced)]
    [InlineData("gaming", PowerMode.Enthusiast)]
    [InlineData("TURBO", PowerMode.Beast)]
    [InlineData("2", PowerMode.Beast)]
    public void ParsesModeNames(string text, PowerMode expected)
    {
        Assert.True(PowerModes.TryParse(text, out var mode));
        Assert.Equal(expected, mode);
    }
}
