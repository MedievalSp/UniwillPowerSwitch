using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UwPower.Core.Automation;

namespace UwPower.Core.Ipc;

/// <summary>
/// Named pipe between the CLI and the running tray app. The Control Center broker allows only one
/// connection per client ID, so while the tray app is running the CLI asks it to do the work instead
/// of connecting itself. One JSON request line in, one JSON response line out.
/// </summary>
public static class TrayPipe
{
    public static string Name => $"UwPower-{Environment.UserName}";

    /// <summary>Held by the tray app for its lifetime; also keeps a second copy from starting.</summary>
    public const string InstanceMutexName = @"Local\UwPowerTray";

    public static bool IsTrayRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(InstanceMutexName, out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // exists, owned by the elevated tray app
        }
    }

    /// <summary>Sends a request to the tray app. Returns null if the tray app isn't running.</summary>
    public static async Task<JsonObject?> TrySendAsync(JsonObject request, TimeSpan? timeout = null)
    {
        if (!IsTrayRunning())
            return null;

        // The tray app is running, so don't silently fall back to a direct broker connection: that
        // would kick the tray app's connection (the broker allows one connection per client ID).
        using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The tray app is running but did not respond.");
        }

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(request.ToJsonString().AsMemory(), cts.Token);
        var line = await new StreamReader(pipe, Encoding.UTF8).ReadLineAsync(cts.Token);
        return line is null ? null : JsonNode.Parse(line) as JsonObject;
    }

    /// <summary>Serves requests until <paramref name="ct"/> is cancelled.</summary>
    public static async Task ServeAsync(PowerController controller, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateServer();
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                Log.Write($"Pipe server error: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            _ = Task.Run(() => HandleAsync(pipe, controller, ct), ct);
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        // The tray app usually runs elevated; let the same (non-elevated) user's CLI connect.
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, PowerController controller, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                var reader = new StreamReader(pipe, Encoding.UTF8);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    return;

                JsonObject response;
                try
                {
                    var request = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("Bad request.");
                    response = await DispatchAsync(request, controller);
                }
                catch (Exception ex)
                {
                    response = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
                }
                await writer.WriteLineAsync(response.ToJsonString().AsMemory(), ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Client went away.
            }
        }
    }

    private static async Task<JsonObject> DispatchAsync(JsonObject request, PowerController controller)
    {
        switch ((string?)request["cmd"])
        {
            case "set":
                if (!PowerModes.TryParse((string?)request["mode"], out var mode))
                    throw new ArgumentException("Unknown mode.");
                await controller.SetModeManuallyAsync(mode);
                return await StatusAsync(controller);

            case "reload":
                controller.ReloadConfig();
                return new JsonObject { ["ok"] = true };

            case "status":
                return await StatusAsync(controller);

            default:
                throw new ArgumentException($"Unknown command '{request["cmd"]}'.");
        }
    }

    private static async Task<JsonObject> StatusAsync(PowerController controller)
    {
        var result = new JsonObject
        {
            ["ok"] = true,
            ["connected"] = controller.Connected,
            ["mode"] = controller.CurrentMode?.ToString(),
            ["reason"] = controller.LastDecision?.Reason,
            ["elevated"] = Idle.PowerRequests.CanQuery,
            ["error"] = controller.LastError,
        };
        if (controller.LastIdle is { } idle)
        {
            result["inputIdleSeconds"] = (int)idle.InputIdle.TotalSeconds;
            result["idle"] = idle.IsIdle;
            result["idleBlockers"] = new JsonArray(idle.Blockers.Select(b => (JsonNode?)b).ToArray());
        }
        if (controller.Connected)
        {
            try
            {
                result["details"] = (await controller.GetServiceStatusAsync()).Describe();
            }
            catch (Exception ex)
            {
                result["details"] = $"(status unavailable: {ex.Message})";
            }
        }
        return result;
    }
}
