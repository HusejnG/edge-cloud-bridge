// Program.cs
//
// The "cloud" side of the device bridge: connects to the MQTT broker,
// subscribes to telemetry from the edge agent, and prints a small live
// dashboard -- standing in for what would normally be a database write
// and a real web dashboard.
//
// Uses only System.Text.Json (part of the .NET runtime, not a NuGet
// package) to parse the edge agent's payload -- consistent with the
// project's goal of zero external dependencies.

using System.Text.Json;
using CloudBridge;

string brokerHost = args.Length > 0 ? args[0] : "localhost";
int brokerPort = args.Length > 1 ? int.Parse(args[1]) : 1883;
string topicFilter = args.Length > 2 ? args[2] : "factory/demo/+/telemetry";

Console.WriteLine($"cloud-bridge: connecting to {brokerHost}:{brokerPort}, subscribing to '{topicFilter}'");

using var client = new MqttClient();
await client.ConnectAsync(brokerHost, brokerPort, clientId: "cloud-bridge-dashboard");
await client.SubscribeAsync(topicFilter, qos: 1);

Console.WriteLine("cloud-bridge: subscribed, waiting for telemetry...\n");

var stats = new Dictionary<string, int>();
int faultCount = 0;
int messageCount = 0;

using var cts = new CancellationTokenSource();
// Stop after a fixed run time when invoked non-interactively (e.g. from
// the integration test harness), rather than running forever.
if (args.Length > 3 && int.TryParse(args[3], out int runSeconds))
{
    cts.CancelAfter(TimeSpan.FromSeconds(runSeconds));
}

try
{
    await foreach (var message in client.ReadMessagesAsync(cts.Token))
    {
        messageCount++;
        var reading = JsonSerializer.Deserialize<TelemetryReading>(message.Payload);
        if (reading is null)
        {
            Console.WriteLine($"  [warn] could not parse payload on topic {message.Topic}");
            continue;
        }

        stats[reading.MachineId] = stats.GetValueOrDefault(reading.MachineId) + 1;
        if (reading.Status == "fault") faultCount++;

        string flag = reading.Status == "fault" ? "  <-- FAULT" : "";
        Console.WriteLine(
            $"  [{reading.MachineId}] temp={reading.TemperatureC,6:F2}C  " +
            $"vibration={reading.VibrationMmS,5:F2}mm/s  rpm={reading.Rpm,7:F1}  " +
            $"status={reading.Status}{flag}");
    }
}
catch (OperationCanceledException)
{
    // Expected when the run-time limit above elapses.
}

Console.WriteLine();
Console.WriteLine("=== cloud-bridge summary ===");
Console.WriteLine($"messages received : {messageCount}");
Console.WriteLine($"fault readings     : {faultCount}");
foreach (var (machine, count) in stats)
{
    Console.WriteLine($"  {machine}: {count} readings");
}

// Matches the flat JSON shape published by edge_agent.c. Kept in this
// file rather than a shared schema on purpose -- the C side and the C#
// side are meant to be independent, the same way a real edge device and
// a real cloud service would be written by different teams and only
// agree on the wire format, not on shared source.
internal sealed record TelemetryReading(
    [property: System.Text.Json.Serialization.JsonPropertyName("machine_id")] string MachineId,
    [property: System.Text.Json.Serialization.JsonPropertyName("timestamp")] long Timestamp,
    [property: System.Text.Json.Serialization.JsonPropertyName("temperature_c")] double TemperatureC,
    [property: System.Text.Json.Serialization.JsonPropertyName("vibration_mm_s")] double VibrationMmS,
    [property: System.Text.Json.Serialization.JsonPropertyName("rpm")] double Rpm,
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status);
