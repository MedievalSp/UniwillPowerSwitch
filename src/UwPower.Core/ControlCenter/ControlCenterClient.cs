using UwPower.Core.Config;
using UwPower.Core.Mqtt;

namespace UwPower.Core.ControlCenter;

/// <summary>
/// Talks to the Uniwill "GCUService" that ships with the laptop's drivers, through its local MQTT
/// broker. Sends the same commands the Control Center app sends when you click a mode; the service
/// then applies that mode's CPU power limits, GPU power and fan table.
/// </summary>
public sealed class ControlCenterClient : IAsyncDisposable
{
    private const string FanControlTopic = "Fan/Control";
    private const string FanStatusTopic = "Fan/Status";
    private const string TrayStatusTopic = "Tray/Status";

    private readonly MiniMqttClient _mqtt;
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];

    /// <summary>Raised (on a background thread) when the service announces a mode change on Tray/Status,
    /// whether it was made by Control Center, the Fn hotkey or this app.</summary>
    public event Action<PowerMode>? ModeReported;

    /// <summary>Completes when the connection to the broker drops.</summary>
    public Task Disconnected => _mqtt.Completion;

    private ControlCenterClient(MiniMqttClient mqtt)
    {
        _mqtt = mqtt;
        _mqtt.MessageReceived += OnMessage;
    }

    public static async Task<ControlCenterClient> ConnectAsync(BrokerSettings broker, CancellationToken ct = default)
    {
        var mqtt = await MiniMqttClient.ConnectAsync(
            broker.Host, broker.Port, broker.ClientId, broker.Username, broker.Password, ct);
        var client = new ControlCenterClient(mqtt);
        try
        {
            await mqtt.SubscribeAsync(TrayStatusTopic, ct);
            await mqtt.SubscribeAsync(FanStatusTopic, ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<ControlCenterStatus> GetStatusAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var report = await RequestAsync(
            FanControlTopic, """{"Action":"GETSTATUS"}""",
            r => r.Status is { Mode: not null },
            timeout, ct, "The Control Center service did not answer a status request.");
        return report.Status!;
    }

    /// <summary>Switches mode and waits until the service confirms it.</summary>
    public Task SetModeAsync(PowerMode mode, TimeSpan? timeout = null, CancellationToken ct = default) =>
        RequestAsync(
            FanControlTopic, $$"""{"Action":"{{PowerModes.ServiceAction(mode)}}"}""",
            r => r.Mode == mode,
            timeout, ct, $"The Control Center service did not confirm the switch to {mode}.");

    public ValueTask DisposeAsync() => _mqtt.DisposeAsync();

    private async Task<Report> RequestAsync(
        string topic, string payload, Func<Report, bool> match, TimeSpan? timeout, CancellationToken ct, string timeoutMessage)
    {
        var waiter = new Waiter(match);
        lock (_gate)
            _waiters.Add(waiter);
        try
        {
            await _mqtt.PublishAsync(topic, payload, ct);
            return await waiter.Completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(timeoutMessage);
        }
        finally
        {
            lock (_gate)
                _waiters.Remove(waiter);
        }
    }

    private void OnMessage(string topic, string payload)
    {
        Report report;
        if (topic == TrayStatusTopic)
        {
            report = new Report(StatusParser.ParseTrayStatus(payload), null, IsModeChange: true);
        }
        else if (topic == FanStatusTopic)
        {
            // Replies to GETSTATUS (from any client). Right after a switch these can still carry the
            // previous mode, so they answer status requests but don't count as mode changes.
            var status = StatusParser.ParseFanStatus(payload);
            report = new Report(status.Mode, status, IsModeChange: false);
        }
        else
        {
            return;
        }

        lock (_gate)
        {
            foreach (var waiter in _waiters)
            {
                if (waiter.Match(report))
                    waiter.Completion.TrySetResult(report);
            }
        }

        if (report.IsModeChange && report.Mode is { } mode)
            ModeReported?.Invoke(mode);
    }

    private sealed record Report(PowerMode? Mode, ControlCenterStatus? Status, bool IsModeChange);

    private sealed class Waiter(Func<Report, bool> match)
    {
        public Func<Report, bool> Match { get; } = match;

        public TaskCompletionSource<Report> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
