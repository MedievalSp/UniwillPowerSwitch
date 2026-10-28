using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UwPower.Core.Idle;

/// <summary>An active Windows power request, as listed by <c>powercfg /requests</c>.</summary>
public sealed record PowerRequest(string Category, string Kind, string Name, string? Reason)
{
    /// <summary>Requests in these categories stop Windows from sleeping or turning off the display.</summary>
    public static readonly string[] BlockingCategories = ["DISPLAY", "SYSTEM", "AWAYMODE"];

    public bool BlocksIdle => BlockingCategories.Contains(Category);

    public string ShortName => Kind == "PROCESS" ? Path.GetFileName(Name) : Name;

    public override string ToString() => $"{ShortName}{(Reason is null ? "" : $" ({Reason})")} [{Category}]";
}

/// <summary>
/// Reads the power requests apps use to keep Windows awake: video players and browsers playing
/// video, games, calls and audio streams. This is the signal Windows itself uses to decide whether
/// the PC is idle enough to sleep. Windows only lets elevated processes list them.
/// </summary>
public static partial class PowerRequests
{
    public static bool CanQuery => NativeIdle.IsElevated;

    public static IReadOnlyList<PowerRequest> Query()
    {
        var psi = new ProcessStartInfo("powercfg.exe", "/requests")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start powercfg.");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            throw new TimeoutException("powercfg /requests did not finish.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"powercfg /requests failed (exit code {process.ExitCode}); it needs administrator rights.");
        return Parse(output.Result);
    }

    /// <summary>
    /// Parses powercfg output. Section headers ("DISPLAY:") are not localized; each request is a
    /// "[KIND] name" line, optionally followed by a line with the reason the app gave.
    /// </summary>
    public static IReadOnlyList<PowerRequest> Parse(string text)
    {
        var results = new List<PowerRequest>();
        string? category = null;
        (string Kind, string Name)? pending = null;

        void Flush(string? reason)
        {
            if (pending is { } p && category is not null)
                results.Add(new PowerRequest(category, p.Kind, p.Name, reason));
            pending = null;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                Flush(null);
                continue;
            }

            var header = HeaderPattern().Match(line);
            if (header.Success)
            {
                Flush(null);
                category = header.Groups[1].Value;
                continue;
            }

            var entry = EntryPattern().Match(line);
            if (entry.Success)
            {
                Flush(null);
                pending = (entry.Groups[1].Value.ToUpperInvariant(), entry.Groups[2].Value.Trim());
                continue;
            }

            // Anything else right after an entry is that entry's reason; elsewhere it's "None." text.
            if (pending is not null)
                Flush(line);
        }
        Flush(null);
        return results;
    }

    [GeneratedRegex(@"^([A-Z]+):$")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^\[(\w+)\]\s*(.+)$")]
    private static partial Regex EntryPattern();
}
