// Program.cs (MqttProtocolTests)
//
// A minimal hand-rolled test runner that keeps the project free of
// external packages. Every check is a real, independent assertion; only
// the small assert-and-report harness around them is homemade.

using CloudBridge;

int passed = 0;
int failed = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"[PASS] {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}" + (detail is null ? "" : $" -- {detail}"));
    }
}

// ---- Remaining Length variable-length encoding ------------------------------
// MQTT's own worked examples (from the OASIS spec) make good test vectors:
// 0 -> [0x00], 127 -> [0x7F], 128 -> [0x80, 0x01], 16384 -> [0x80, 0x80, 0x01]

{
    byte[] encoded = MqttClient.EncodeRemainingLength(0);
    Check("encode(0) == [0x00]", encoded.SequenceEqual(new byte[] { 0x00 }));
}
{
    byte[] encoded = MqttClient.EncodeRemainingLength(127);
    Check("encode(127) == [0x7F]", encoded.SequenceEqual(new byte[] { 0x7F }));
}
{
    byte[] encoded = MqttClient.EncodeRemainingLength(128);
    Check("encode(128) == [0x80, 0x01]", encoded.SequenceEqual(new byte[] { 0x80, 0x01 }));
}
{
    byte[] encoded = MqttClient.EncodeRemainingLength(16384);
    Check("encode(16384) == [0x80, 0x80, 0x01]",
          encoded.SequenceEqual(new byte[] { 0x80, 0x80, 0x01 }));
}

// Round-trip: for a range of values, encoding then decoding must recover
// the original value and consume exactly the bytes produced.
foreach (int value in new[] { 0, 1, 127, 128, 300, 16383, 16384, 2097151, 2097152 })
{
    byte[] encoded = MqttClient.EncodeRemainingLength(value);
    (int decoded, int consumed) = MqttClient.DecodeRemainingLength(encoded);
    Check($"round-trip remaining length {value}",
          decoded == value && consumed == encoded.Length,
          $"got value={decoded} consumed={consumed}, expected value={value} consumed={encoded.Length}");
}

Check("decoding throws on truncated input",
      ThrowsArgumentException(() => MqttClient.DecodeRemainingLength(new byte[] { 0x80 })));

// ---- PUBLISH body parsing (the bug this project actually shipped with) ------
//
// The real bug: forgetting that QoS > 0 PUBLISH packets carry a 2-byte
// packet identifier between the topic and the payload. These tests pin
// that behavior down explicitly so it can't silently regress.

{
    // QoS 0: topic length(2) + topic + payload directly, no packet id.
    byte[] body = BuildPublishBody(topic: "a/b", packetId: null, payload: "{}"u8.ToArray());
    (string topic, byte[] payload) = MqttClient.ParsePublishBody(body, qos: 0);
    Check("QoS 0 publish: topic parsed correctly", topic == "a/b");
    Check("QoS 0 publish: payload parsed correctly (no packet id to skip)",
          System.Text.Encoding.UTF8.GetString(payload) == "{}");
}
{
    // QoS 1: topic length(2) + topic + packet id(2) + payload.
    byte[] payloadBytes = """{"machine_id":"press-01","status":"ok"}"""u8.ToArray();
    byte[] body = BuildPublishBody(topic: "factory/demo/press-01/telemetry",
                                    packetId: 1, payload: payloadBytes);
    (string topic, byte[] payload) = MqttClient.ParsePublishBody(body, qos: 1);
    Check("QoS 1 publish: topic parsed correctly", topic == "factory/demo/press-01/telemetry");
    Check("QoS 1 publish: packet identifier correctly skipped",
          payload.SequenceEqual(payloadBytes),
          "payload would be offset by 2 bytes if the packet id weren't skipped -- " +
          "this is exactly the bug found when first wiring this up against a real broker");
}

// ---- PUBACK and PINGREQ ------------------------------------------------------
// Without PUBACK the broker never releases QoS 1 messages; without PINGREQ
// it drops an otherwise idle connection after 1.5 x keep-alive.

{
    byte[] body = BuildPublishBody(topic: "a/b", packetId: 0x0102, payload: "{}"u8.ToArray());
    Check("QoS 1 publish: packet identifier read correctly",
          MqttClient.ReadPublishPacketId(body) == 0x0102);
}
{
    byte[] puback = MqttClient.BuildPubAckPacket(0x1234);
    Check("PUBACK == [0x40, 0x02, id-high, id-low]",
          puback.SequenceEqual(new byte[] { 0x40, 0x02, 0x12, 0x34 }));
}
{
    byte[] pingreq = MqttClient.BuildPingReqPacket();
    Check("PINGREQ == [0xC0, 0x00]", pingreq.SequenceEqual(new byte[] { 0xC0, 0x00 }));
}

Console.WriteLine();
Console.WriteLine($"{passed}/{passed + failed} checks passed");
return failed == 0 ? 0 : 1;

// ---- test helpers -----------------------------------------------------------

static bool ThrowsArgumentException(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
}

static byte[] BuildPublishBody(string topic, ushort? packetId, byte[] payload)
{
    byte[] topicBytes = System.Text.Encoding.UTF8.GetBytes(topic);
    using var stream = new MemoryStream();
    stream.WriteByte((byte)(topicBytes.Length >> 8));
    stream.WriteByte((byte)(topicBytes.Length & 0xFF));
    stream.Write(topicBytes);
    if (packetId is ushort id)
    {
        stream.WriteByte((byte)(id >> 8));
        stream.WriteByte((byte)(id & 0xFF));
    }
    stream.Write(payload);
    return stream.ToArray();
}
