using UwPower.Core.Config;

namespace UwPower.Core.Idle;

public sealed record IdleSnapshot(
    TimeSpan InputIdle,
    TimeSpan Threshold,
    bool IsIdle,
    IReadOnlyList<string> Blockers,
    string? Note);

/// <summary>
/// Decides whether the user is away, the way Windows decides whether it may sleep: no input for the
/// configured time AND no app holding a display/system power request. Optionally a fullscreen app
/// in front also counts as activity (covers games that don't register a request).
/// </summary>
public sealed class IdleDetector
{
    private static readonly TimeSpan RequestCacheTime = TimeSpan.FromSeconds(30);

    private readonly Func<TimeSpan> _inputIdle;
    private readonly Func<string?> _fullscreenState;
    private readonly Func<bool> _canQueryRequests;
    private readonly Func<IReadOnlyList<PowerRequest>> _queryRequests;

    private IReadOnlyList<PowerRequest>? _cachedRequests;
    private DateTime _cachedAt;

    public IdleDetector()
        : this(NativeIdle.InputIdle, NativeIdle.FullscreenAppState, () => PowerRequests.CanQuery, PowerRequests.Query)
    {
    }

    internal IdleDetector(
        Func<TimeSpan> inputIdle,
        Func<string?> fullscreenState,
        Func<bool> canQueryRequests,
        Func<IReadOnlyList<PowerRequest>> queryRequests)
    {
        _inputIdle = inputIdle;
        _fullscreenState = fullscreenState;
        _canQueryRequests = canQueryRequests;
        _queryRequests = queryRequests;
    }

    /// <param name="settings">Idle settings.</param>
    /// <param name="checkBlockersAlways">Check power requests/fullscreen even before the input
    /// threshold is reached (for diagnostics). Normally they're only checked once input is idle,
    /// which keeps the cost near zero while you're using the PC.</param>
    public IdleSnapshot Evaluate(IdleSettings settings, bool checkBlockersAlways = false)
    {
        var inputIdle = _inputIdle();
        var threshold = TimeSpan.FromMinutes(Math.Max(0.05, settings.Minutes));
        var inputIsIdle = inputIdle >= threshold;
        var blockers = new List<string>();
        string? note = null;

        if (!inputIsIdle)
            _cachedRequests = null; // re-read fresh the next time input goes idle

        if (inputIsIdle || checkBlockersAlways)
        {
            if (settings.FullscreenCountsAsActive && _fullscreenState() is { } fullscreen)
                blockers.Add(fullscreen);

            if (settings.RespectPowerRequests)
            {
                if (!_canQueryRequests())
                {
                    note = "Not running as administrator, so apps keeping the PC awake (videos, games, calls) " +
                           "can't be detected; only keyboard/mouse input and fullscreen apps count.";
                }
                else
                {
                    try
                    {
                        if (_cachedRequests is null || DateTime.UtcNow - _cachedAt > RequestCacheTime)
                        {
                            _cachedRequests = _queryRequests();
                            _cachedAt = DateTime.UtcNow;
                        }
                        blockers.AddRange(_cachedRequests
                            .Where(r => r.BlocksIdle && !IsIgnored(r, settings.IgnoredRequests))
                            .Select(r => r.ToString()));
                    }
                    catch (Exception ex)
                    {
                        note = $"Could not read power requests: {ex.Message}";
                    }
                }
            }
        }

        return new IdleSnapshot(inputIdle, threshold, inputIsIdle && blockers.Count == 0, blockers, note);
    }

    private static bool IsIgnored(PowerRequest request, IEnumerable<string> ignored) =>
        ignored.Any(text => !string.IsNullOrWhiteSpace(text) &&
            (request.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             (request.Reason?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)));
}
