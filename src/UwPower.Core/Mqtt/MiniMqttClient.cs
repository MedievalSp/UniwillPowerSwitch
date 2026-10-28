using System.Net.Sockets;

namespace UwPower.Core.Mqtt;

public sealed class MqttException(string message) : Exception(message);

/// <summary>A tiny MQTT 3.1.1 client (QoS 0) - enough to talk to Control Center's local broker.</summary>
internal sealed class MiniMqttClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Timer? _pingTimer;
    private int _nextPacketId;

    public event Action<string, string>? MessageReceived;

    /// <summary>Completes when the connection drops or is closed.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    private MiniMqttClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static async Task<MiniMqttClient> ConnectAsync(
        string host, int port, string clientId, string username, string password, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await tcp.ConnectAsync(host, port, timeout.Token);
            var client = new MiniMqttClient(tcp);
            await client.WriteAsync(MqttPacket.Connect(clientId, username, password, 60), timeout.Token);

            var (type, _, body) = await client.ReadPacketAsync(timeout.Token);
            if (type != MqttPacket.TypeConnAck || body.Length < 2)
                throw new MqttException("Unexpected reply from the Control Center broker.");
            if (body[1] != 0)
                throw new MqttException(DescribeRefusal(body[1]));

            client.Completion = Task.Run(client.ReadLoopAsync);
            client._pingTimer = new Timer(_ => client.Ping(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new MqttException($"Timed out connecting to the Control Center broker at {host}:{port}.");
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new MqttException(
                $"Could not reach the Control Center broker at {host}:{port} ({ex.SocketErrorCode}). " +
                "Is the GCUBridge service running?");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public Task SubscribeAsync(string topic, CancellationToken ct = default)
    {
        var id = (ushort)(Interlocked.Increment(ref _nextPacketId) % ushort.MaxValue + 1);
        return WriteAsync(MqttPacket.Subscribe(id, topic), ct);
    }

    public Task PublishAsync(string topic, string payload, CancellationToken ct = default) =>
        WriteAsync(MqttPacket.Publish(topic, payload), ct);

    public async ValueTask DisposeAsync()
    {
        _pingTimer?.Dispose();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await WriteAsync(MqttPacket.Disconnect, timeout.Token);
        }
        catch
        {
            // Connection may already be gone.
        }
        _cts.Cancel();
        _tcp.Dispose();
        try
        {
            await Completion;
        }
        catch
        {
            // Read loop ends with an error once the socket is closed.
        }
        _cts.Dispose();
    }

    private static string DescribeRefusal(byte code) => code switch
    {
        2 => "The Control Center broker rejected the client ID (code 2). Only client IDs built into Control Center are accepted.",
        4 => "The Control Center broker rejected the username/password (code 4).",
        5 => "The Control Center broker refused the connection: not authorized (code 5).",
        _ => $"The Control Center broker refused the connection (code {code}).",
    };

    private void Ping()
    {
        _ = WriteAsync(MqttPacket.PingReq, CancellationToken.None).ContinueWith(
            t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task WriteAsync(byte[] packet, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(packet, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var (type, flags, body) = await ReadPacketAsync(_cts.Token);
            if (type != MqttPacket.TypePublish)
                continue;

            var (topic, payload) = MqttPacket.ParsePublish(flags, body);
            try
            {
                MessageReceived?.Invoke(topic, payload);
            }
            catch (Exception ex)
            {
                Log.Write($"MQTT message handler failed for {topic}: {ex.Message}");
            }
        }
    }

    private async Task<(int Type, int Flags, byte[] Body)> ReadPacketAsync(CancellationToken ct)
    {
        var header = new byte[1];
        await _stream.ReadExactlyAsync(header, ct);

        int length = 0, multiplier = 1;
        var one = new byte[1];
        while (true)
        {
            await _stream.ReadExactlyAsync(one, ct);
            length += (one[0] & 0x7F) * multiplier;
            if ((one[0] & 0x80) == 0)
                break;
            multiplier *= 128;
            if (multiplier > 128 * 128 * 128)
                throw new MqttException("Malformed packet length.");
        }

        var body = new byte[length];
        if (length > 0)
            await _stream.ReadExactlyAsync(body, ct);
        return (header[0] >> 4, header[0] & 0x0F, body);
    }
}
