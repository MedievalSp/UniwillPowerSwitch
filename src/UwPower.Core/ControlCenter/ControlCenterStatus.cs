using System.Text.Json;

namespace UwPower.Core.ControlCenter;

public sealed record ControlCenterStatus(
    PowerMode? Mode,
    string? ProfileName,
    int? CpuPowerLimitW,
    int? GpuTgpW,
    int? GpuDynamicBoostW,
    string? FanTable)
{
    public string Describe()
    {
        var parts = new List<string> { Mode?.ToString() ?? "Unknown" };
        if (CpuPowerLimitW is { } cpu)
            parts.Add($"CPU {cpu} W");
        if (GpuTgpW is { } gpu)
            parts.Add($"GPU {gpu} W");
        else if (GpuDynamicBoostW is { } boost)
            parts.Add($"GPU default TGP + {boost} W boost");
        if (FanTable is not null)
            parts.Add($"fan table {FanTable}");
        return string.Join(", ", parts);
    }
}

/// <summary>Parses the JSON the service publishes. Values arrive as strings, numbers or null.</summary>
internal static class StatusParser
{
    public static PowerMode? ParseTrayStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadMode(doc.RootElement);
    }

    public static ControlCenterStatus ParseFanStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var isAmd = ReadBool(root, "IsAMDPlatform") ?? false;
        var tgpOn = ReadInt(root, "GPU_ConfigurableTGPSwitch") == 1;
        var boostOn = ReadInt(root, "GPU_DynamicBoostSwitch") == 1;

        return new ControlCenterStatus(
            ReadMode(root),
            ReadString(root, "ProfileName"),
            ReadInt(root, isAmd ? "CPU_AmdSPL" : "CPU_PL1"),
            tgpOn ? ReadInt(root, "GPU_ConfigurableTGPTarget") : null,
            boostOn ? ReadInt(root, "GPU_DynamicBoost") : null,
            ReadString(root, "FAN_TableName"));
    }

    private static PowerMode? ReadMode(JsonElement root) =>
        ReadInt(root, "OperatingMode") is { } v && Enum.IsDefined(typeof(PowerMode), v) ? (PowerMode)v : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e))
            return null;
        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(e.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static bool? ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e))
            return null;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => e.GetString() is "1" or "true" or "True",
            JsonValueKind.Number => e.TryGetInt32(out var n) && n != 0,
            _ => null,
        };
    }
}
