// MqttClient.cs
//
// A minimal MQTT 3.1.1 client, hand-written over a raw TCP socket rather
// than pulled in as a package. Two reasons for that, both deliberate:
//
//   1. No external dependency needed for what this project actually uses
//      (CONNECT, SUBSCRIBE, and receiving PUBLISH) - pulling in a full
//      client library for three packet types is disproportionate.
//   2. Implementing the wire format by hand is the same skill this whole
//      portfolio is built around (see bit-protocol-parser): understanding
//      a binary protocol well enough to encode and decode it yourself,
//      not just call something that already does.
//
// This implements just enough of MQTT 3.1.1 (OASIS standard) to connect,
// subscribe to one topic, and receive PUBLISH packets: CONNECT/CONNACK,
// SUBSCRIBE/SUBACK, PUBLISH. It does not implement QoS 1/2 acknowledgment
// flows, PINGREQ keep-alive, or clean unsubscribe - noted as a
// deliberate scope limit, not an oversight, in the README.

using System.Net.Sockets;
using System.Text;

namespace CloudBridge;

public enum MqttPacketType : byte
{
    Connect = 1,
    ConnAck = 2,
    Publish = 3,
    Subscribe = 8,
    SubAck = 9,
}

public sealed record MqttMessage(string Topic, byte[] Payload)
{
    public string PayloadAsString() => Encoding.UTF8.GetString(Payload);
}

public sealed class MqttClient : IDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;

    public async Task ConnectAsync(string host, int port, string clientId)
    {
        await _tcp.ConnectAsync(host, port);
        _stream = _tcp.GetStream();

        byte[] packet = BuildConnectPacket(clientId);
        await _stream.WriteAsync(packet);

        byte[] connAck = await ReadFixedLengthPacketAsync(expectedRemainingLength: 2);
        // CONNACK payload: [session-present-flag, return-code]. Return
        // code 0 means the connection was accepted.
        if (connAck[1] != 0x00)
        {
            throw new IOException($"MQTT CONNECT rejected, return code {connAck[1]}");
        }
    }

    public async Task SubscribeAsync(string topicFilter, byte qos = 0)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        ushort packetId = 1;
        byte[] packet = BuildSubscribePacket(packetId, topicFilter, qos);
        await _stream.WriteAsync(packet);

        // SUBACK: variable header (2-byte packet id) + payload (granted QoS
        // per requested topic, 1 byte here since we subscribed to one).
        await ReadFixedLengthPacketAsync(expectedRemainingLength: 3);
    }

    /// Reads and yields PUBLISH messages until the stream closes or the
    /// cancellation token fires. Ignores any packet type that isn't
    /// PUBLISH (e.g. a future PINGRESP), which is safe because the fixed
    /// header always tells us exactly how many bytes to skip.
    public async IAsyncEnumerable<MqttMessage> ReadMessagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        while (!ct.IsCancellationRequested)
        {
            int firstByte = await ReadByteAsync(ct);
            if (firstByte < 0) yield break; // stream closed

            var packetType = (MqttPacketType)((firstByte >> 4) & 0x0F);
            int qos = (firstByte >> 1) & 0x03;
            int remainingLength = await ReadRemainingLengthAsync(ct);
            byte[] body = await ReadExactAsync(remainingLength, ct);

            if (packetType != MqttPacketType.Publish) continue;

            (string topic, byte[] payload) = ParsePublishBody(body, qos);
            yield return new MqttMessage(topic, payload);
        }
    }

    // ---- packet construction -------------------------------------------------

    private static byte[] BuildConnectPacket(string clientId)
    {
        byte[] protocolName = Encoding.UTF8.GetBytes("MQTT");
        byte[] clientIdBytes = Encoding.UTF8.GetBytes(clientId);

        using var variableAndPayload = new MemoryStream();
        WithLengthPrefix(variableAndPayload, protocolName);
        variableAndPayload.WriteByte(0x04); // protocol level: MQTT 3.1.1
        variableAndPayload.WriteByte(0x02); // connect flags: clean session
        WriteUInt16BigEndian(variableAndPayload, 60); // keep-alive seconds
        WithLengthPrefix(variableAndPayload, clientIdBytes);

        return WrapWithFixedHeader(MqttPacketType.Connect, flags: 0x00,
                                    body: variableAndPayload.ToArray());
    }

    private static byte[] BuildSubscribePacket(ushort packetId, string topicFilter, byte qos)
    {
        byte[] topicBytes = Encoding.UTF8.GetBytes(topicFilter);

        using var body = new MemoryStream();
        WriteUInt16BigEndian(body, packetId);
        WithLengthPrefix(body, topicBytes);
        body.WriteByte(qos);

        // SUBSCRIBE packets require flags 0x02 per the MQTT 3.1.1 spec -
        // one of the handful of fixed-header quirks that only shows up
        // once you implement the wire format instead of reading about it.
        return WrapWithFixedHeader(MqttPacketType.Subscribe, flags: 0x02,
                                    body: body.ToArray());
    }

    private static byte[] WrapWithFixedHeader(MqttPacketType type, byte flags, byte[] body)
    {
        using var packet = new MemoryStream();
        packet.WriteByte((byte)(((byte)type << 4) | flags));
        WriteRemainingLength(packet, body.Length);
        packet.Write(body);
        return packet.ToArray();
    }

    // ---- packet parsing --------------------------------------------------------

    internal static (string Topic, byte[] Payload) ParsePublishBody(byte[] body, int qos)
    {
        // PUBLISH variable header: 2-byte topic length, topic bytes, then
        // - only when QoS > 0 - a 2-byte packet identifier before the
        // payload starts. Missing this for QoS 1/2 is an easy mistake:
        // the payload silently shifts by 2 bytes and whatever parses it
        // next (here, JSON) fails on what looks like garbage at the
        // front. That's exactly what happened the first time this was
        // wired up against a QoS 1 publish - see the project README.
        int topicLen = (body[0] << 8) | body[1];
        string topic = Encoding.UTF8.GetString(body, 2, topicLen);
        int payloadStart = 2 + topicLen;
        if (qos > 0)
        {
            payloadStart += 2; // skip the packet identifier
        }
        byte[] payload = body[payloadStart..];
        return (topic, payload);
    }

    // ---- MQTT variable-length "Remaining Length" encoding -----------------------
    //
    // MQTT encodes the body length as 1-4 bytes: 7 bits of value per byte,
    // top bit set to mean "more bytes follow". This is the same family of
    // problem as the bit-packing in bit-protocol-parser - a compact,
    // self-describing encoding for a value of unknown width in advance.

    internal static void WriteRemainingLength(Stream stream, int length)
    {
        foreach (byte b in EncodeRemainingLength(length))
        {
            stream.WriteByte(b);
        }
    }

    /// Pure form of the encoder, directly testable without a Stream.
    internal static byte[] EncodeRemainingLength(int length)
    {
        var bytes = new List<byte>();
        do
        {
            byte encodedByte = (byte)(length % 128);
            length /= 128;
            if (length > 0) encodedByte |= 0x80;
            bytes.Add(encodedByte);
        } while (length > 0);
        return bytes.ToArray();
    }

    /// Pure form of the decoder: given bytes already read from the wire,
    /// returns the decoded value and how many bytes it consumed. Directly
    /// testable without faking a network stream.
    internal static (int Value, int BytesConsumed) DecodeRemainingLength(ReadOnlySpan<byte> bytes)
    {
        int multiplier = 1;
        int value = 0;
        int consumed = 0;
        byte encodedByte;
        do
        {
            if (consumed >= bytes.Length)
            {
                throw new ArgumentException("Not enough bytes to decode a remaining-length value.");
            }
            encodedByte = bytes[consumed];
            value += (encodedByte & 0x7F) * multiplier;
            multiplier *= 128;
            consumed++;
        } while ((encodedByte & 0x80) != 0);
        return (value, consumed);
    }

    private async Task<int> ReadRemainingLengthAsync(CancellationToken ct)
    {
        int multiplier = 1;
        int value = 0;
        byte encodedByte;
        do
        {
            int b = await ReadByteAsync(ct);
            if (b < 0) throw new IOException("Stream closed while reading remaining length.");
            encodedByte = (byte)b;
            value += (encodedByte & 0x7F) * multiplier;
            multiplier *= 128;
        } while ((encodedByte & 0x80) != 0);
        return value;
    }

    // ---- small stream helpers --------------------------------------------------

    private static void WithLengthPrefix(Stream stream, byte[] data)
    {
        WriteUInt16BigEndian(stream, (ushort)data.Length);
        stream.Write(data);
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private async Task<byte[]> ReadFixedLengthPacketAsync(int expectedRemainingLength)
    {
        await ReadByteAsync(default); // discard the fixed-header type/flags byte
        int remaining = await ReadRemainingLengthAsync(default);
        byte[] body = await ReadExactAsync(remaining, default);
        if (remaining < expectedRemainingLength)
        {
            throw new IOException("Unexpected short packet from broker.");
        }
        return body;
    }

    private async Task<int> ReadByteAsync(CancellationToken ct)
    {
        var buffer = new byte[1];
        int n = await _stream!.ReadAsync(buffer.AsMemory(0, 1), ct);
        return n == 0 ? -1 : buffer[0];
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await _stream!.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n == 0) throw new IOException("Stream closed mid-packet.");
            offset += n;
        }
        return buffer;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp.Dispose();
    }
}
