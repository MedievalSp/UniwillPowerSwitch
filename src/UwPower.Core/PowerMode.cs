namespace UwPower.Core;

/// <summary>The three operating modes. Values match Control Center's "OperatingMode" field.</summary>
public enum PowerMode
{
    Balanced = 0,
    Enthusiast = 1,
    Beast = 2,
}

public static class PowerModes
{
    public static readonly PowerMode[] All = [PowerMode.Balanced, PowerMode.Enthusiast, PowerMode.Beast];

    // Control Center's service uses its own names for the same modes: Office / Gaming / Turbo.
    internal static string ServiceAction(PowerMode mode) => mode switch
    {
        PowerMode.Balanced => "OPERATING_OFFICE_MODE",
        PowerMode.Enthusiast => "OPERATING_GAMING_MODE",
        PowerMode.Beast => "OPERATING_TURBO_MODE",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Accepts Eluktronics names, Uniwill internal names, or the numeric value.</summary>
    public static bool TryParse(string? text, out PowerMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "balanced" or "office" or "0":
                mode = PowerMode.Balanced;
                return true;
            case "enthusiast" or "gaming" or "1":
                mode = PowerMode.Enthusiast;
                return true;
            case "beast" or "turbo" or "2":
                mode = PowerMode.Beast;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
