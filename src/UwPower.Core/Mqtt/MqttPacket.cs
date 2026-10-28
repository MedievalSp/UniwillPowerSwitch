using System.Text;

namespace UwPower.Core.Mqtt;

/// <summary>Encoding/decoding for the small subset of MQTT 3.1.1 the service needs (QoS 0 only).</summary>
internal static class MqttPacket
{
    public const int TypeConnAck = 2;
    public const int TypePublish = 3;
    public const int TypeSubAck = 9;
    public const int TypePingResp = 13;

    public static readonly byte[] PingReq = [0xC0, 0x00];
    public static readonly byte[] Disconnect = [0xE0, 0x00];

    public static byte[] Connect(string clientId, string username, string password, ushort keepAliveSeconds)
    {
        var body = new List<byte>();
        WriteString(body, "MQTT");
        body.Add(4);    // protocol level 3.1.1
        body.Add(0xC2); // username + password + clean session
        body.Add((byte)(keepAliveSeconds >> 8));
        body.Add((byte)keepAliveSeconds);
        WriteString(body, clientId);
        WriteString(body, username);
        WriteString(body, password);
        return Frame(0x10, body);
    }

    public static byte[] Publish(string topic, string payload)
    {
        var body = new List<byte>();
        WriteString(body, topic);
        body.AddRange(Encoding.UTF8.GetBytes(payload));
        return Frame(0x30, body);
    }

    public static byte[] Subscribe(ushort packetId, string topic)
    {
        var body = new List<byte> { (byte)(packetId >> 8), (byte)packetId };
        WriteString(body, topic);
        body.Add(0); // QoS 0
        return Frame(0x82, body);
    }

    public static byte[] EncodeRemainingLength(int length)
    {
        var bytes = new List<byte>(4);
        do
        {
            var digit = (byte)(length % 128);
            length /= 128;
            if (length > 0)
                digit |= 0x80;
            bytes.Add(digit);
        } while (length > 0);
        return bytes.ToArray();
    }

    public static (string Topic, string Payload) ParsePublish(int flags, ReadOnlySpan<byte> body)
    {
        var topicLength = (body[0] << 8) | body[1];
        var topic = Encoding.UTF8.GetString(body.Slice(2, topicLength));
        var offset = 2 + topicLength;
        if (((flags >> 1) & 3) > 0)
            offset += 2; // packet identifier present for QoS 1/2
        return (topic, Encoding.UTF8.GetString(body[offset..]));
    }

    private static byte[] Frame(byte header, List<byte> body)
    {
        var packet = new List<byte>(body.Count + 5) { header };
        packet.AddRange(EncodeRemainingLength(body.Count));
        packet.AddRange(body);
        return packet.ToArray();
    }

    private static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        buffer.Add((byte)(bytes.Length >> 8));
        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
    }
}
