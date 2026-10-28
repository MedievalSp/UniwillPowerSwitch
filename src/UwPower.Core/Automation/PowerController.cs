using UwPower.Core.Config;
using UwPower.Core.ControlCenter;
using UwPower.Core.Idle;

namespace UwPower.Core.Automation;

/// <summary>
/// The long-running part of the tray app: keeps a connection to the Control Center service, watches
/// idle state and the schedule every few seconds, and switches modes when the decision changes.
/// Mode changes made elsewhere (Control Center, Fn hotkey) are treated as manual choices.
/// </summary>
public sealed class PowerController : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly IdleDetector _idleDetector = new();
    private readonly CancellationTokenSource _cts = new();
    private ModeEngine _engine;
    private ControlCenterClient? _client;
    private PowerMode? _pendingMode;
    private Task _loop = Task.CompletedTask;
    private TaskCompletionSource _wake = NewWake();

    public PowerController()
    {
        _engine = new ModeEngine(ConfigStore.Load());
    }

    /// <summary>Raised on a background thread whenever anything shown in the UI may have changed.</summary>
    public event Action? StateChanged;

    public bool Connected { get; private set; }

    public PowerMode? CurrentMode { get; private set; }

    public ModeDecision? LastDecision { get; private set; }

    public IdleSnapshot? LastIdle { get; private set; }

    public string? LastError { get; private set; }

    public AppConfig Config
    {
        get
        {
            lock (_gate)
                return _engine.Config;
        }
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Raised (possibly on a background thread) after <see cref="ReloadConfig"/>.</summary>
    public event Action? ConfigReloaded;

    /// <summary>Re-reads config.json (after the CLI or the settings window changed it).</summary>
    public void ReloadConfig()
    {
        var config = ConfigStore.Load();
        lock (_gate)
            _engine.UpdateConfig(config);
        Log.Write("Configuration reloaded.");
        ConfigReloaded?.Invoke();
        Wake();
    }

    /// <summary>Switches mode because you asked for it (tray menu, CLI, settings window).</summary>
    public async Task SetModeManuallyAsync(PowerMode mode)
    {
        RecordManualSelection(mode);
        await ApplyAsync(mode, "manual choice");
    }

    public async Task<ControlCenterStatus> GetServiceStatusAsync()
    {
        var client = _client ?? throw new InvalidOperationException(NotConnectedMessage());
        return await client.GetStatusAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Wake();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        if (_client is not null)
            await _client.DisposeAsync();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);
                retryDelay = TimeSpan.FromSeconds(2);

                while (!ct.IsCancellationRequested && !_client!.Disconnected.IsCompleted)
                {
                    await TickAsync(ct);
                    var wake = _wake.Task;
                    await Task.WhenAny(Task.Delay(TickInterval, ct), _client.Disconnected, wake);
                    if (wake.IsCompleted)
                        _wake = NewWake();
                }
                if (!ct.IsCancellationRequested)
                    SetError("Lost connection to the Control Center service; reconnecting.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }

            await DropClientAsync();
            try
            {
                await Task.Delay(retryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var client = await ControlCenterClient.ConnectAsync(Config.Broker, ct);
        client.ModeReported += OnModeReported;
        _client = client;

        var status = await client.GetStatusAsync(ct: ct);
        CurrentMode = status.Mode;
        Connected = true;
        LastError = null;
        Log.Write($"Connected to Control Center service: {status.Describe()}");

        if (Config.DefaultMode is null && status.Mode is { } mode)
        {
            var saved = ConfigStore.Update(c => c.DefaultMode ??= mode);
            lock (_gate)
                _engine.Config.DefaultMode = saved.DefaultMode;
        }
        RaiseStateChanged();
    }

    private async Task DropClientAsync()
    {
        Connected = false;
        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.ModeReported -= OnModeReported;
            await client.DisposeAsync();
        }
        RaiseStateChanged();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        IdleSettings idleSettings;
        lock (_gate)
            idleSettings = _engine.Config.Idle;

        var idle = _idleDetector.Evaluate(idleSettings);
        ModeDecision decision;
        lock (_gate)
            decision = _engine.Decide(DateTime.Now, idle.IsIdle);

        var changed = LastIdle?.IsIdle != idle.IsIdle || LastDecision != decision;
        LastIdle = idle;
        LastDecision = decision;

        if (decision.Mode is { } target && target != CurrentMode && _pendingMode is null)
        {
            try
            {
                await ApplyAsync(target, decision.Reason, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already logged by ApplyAsync; the next tick retries (or reconnects if the link dropped).
            }
        }
        else if (changed)
            RaiseStateChanged();
    }

    private async Task ApplyAsync(PowerMode mode, string reason, CancellationToken ct = default)
    {
        await _applyLock.WaitAsync(ct);
        try
        {
            var client = _client ?? throw new InvalidOperationException(NotConnectedMessage());
            if (mode == CurrentMode)
                return;

            _pendingMode = mode;
            var previous = CurrentMode;
            await client.SetModeAsync(mode, SwitchTimeout, ct);
            CurrentMode = mode;
            Log.Write($"{previous?.ToString() ?? "Unknown"} -> {mode} ({reason})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError($"Could not switch to {mode}: {ex.Message}");
            throw;
        }
        finally
        {
            _pendingMode = null;
            _applyLock.Release();
            RaiseStateChanged();
        }
    }

    private void OnModeReported(PowerMode mode)
    {
        // Our own switch in flight: the service is just confirming it.
        if (_pendingMode is not null || mode == CurrentMode)
            return;

        var previous = CurrentMode;
        CurrentMode = mode;
        if (previous is null)
            return; // first report after connecting, not a change

        Log.Write($"{previous} -> {mode} (changed outside this app)");
        RecordManualSelection(mode);
        RaiseStateChanged();
    }

    private void RecordManualSelection(PowerMode mode)
    {
        bool saveDefault;
        lock (_gate)
            saveDefault = _engine.OnManualSelection(mode, DateTime.Now, LastIdle?.IsIdle ?? false);
        if (saveDefault)
            ConfigStore.Update(c => c.DefaultMode = mode);
    }

    private void SetError(string message)
    {
        if (message != LastError)
            Log.Write(message);
        LastError = message;
        RaiseStateChanged();
    }

    private string NotConnectedMessage() =>
        LastError is null ? "Not connected to the Control Center service yet." : $"Not connected: {LastError}";

    private void Wake() => _wake.TrySetResult();

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"State change handler failed: {ex.Message}");
        }
    }
}
