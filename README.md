# Edge-Cloud Bridge (C / C#)

A two-language demo mirroring the "device bridge" pattern common in
Industrial IoT: a **C edge agent** simulates a factory-floor sensor and
publishes telemetry over MQTT; a **C# cloud-side service**, with its MQTT
client hand-written over a raw TCP socket, subscribes and displays a live
dashboard. It exercises the C/C# combination that is common in industrial
edge/cloud platforms.

## Why C and C#, and why by hand

**C for the edge side** — real edge agents run on constrained industrial
hardware, and C is what you find there in practice. `edge_agent_core.c`
(the simulation and JSON-building logic) is deliberately separated from
`edge_agent_main.c` (the MQTT/network I/O), so the core logic is unit
tested without needing a broker running.

**C# for the cloud side** — a plausible stand-in for a .NET backend
service that would normally write to a database and drive a web
dashboard.

## Architecture

```
edge-cloud-bridge/
├── edge-agent/               (C)
│   ├── edge_agent_core.h/.c   → simulation + JSON payload building (unit tested)
│   ├── edge_agent_main.c      → MQTT connection/publish loop (libmosquitto)
│   ├── test_edge_agent_core.c → hand-rolled assert-based tests
│   └── CMakeLists.txt
├── cloud-bridge/              (C#)
│   ├── MqttClient.cs           → MQTT 3.1.1 client over a raw TCP socket
│   └── Program.cs              → subscribes, parses JSON, prints a dashboard
├── tests/
│   └── MqttProtocolTests/      → hand-rolled tests for the MQTT wire format
└── .github/workflows/ci.yml
```

```
┌─────────────┐   MQTT PUBLISH    ┌───────────┐   MQTT PUBLISH    ┌──────────────┐
│  edge_agent │ ───────────────►  │ mosquitto │ ───────────────►  │ cloud-bridge │
│    (C)      │                   │  broker   │                   │    (C#)      │
└─────────────┘                   └───────────┘                   └──────────────┘
  simulates a                                                       hand-rolled MQTT
  machine sensor,                                                   client, parses JSON,
  builds JSON by hand                                                prints dashboard
```

## The bug this project actually shipped with

The first end-to-end run threw a JSON parse exception:
`'0x00' is an invalid start of a value`. The cause: an MQTT `PUBLISH`
packet at QoS 1 or 2 carries a 2-byte **packet identifier** between the
topic name and the payload; QoS 0 doesn't. The initial parser always
skipped straight from the topic to the payload, so at QoS 1 the "payload"
actually started 2 bytes early — with the high byte of the packet
identifier, which is `0x00` for small IDs. `System.Text.Json` correctly
rejected a JSON document that starts with a null byte.

The fix (`ParsePublishBody` in `MqttClient.cs`) skips those 2 bytes
whenever QoS > 0. The `"QoS 1 publish: packet identifier correctly skipped"`
check in `tests/MqttProtocolTests/Program.cs` pins the regression down: it
builds a QoS 1 body by hand and verifies that the parsed payload matches
exactly.

## Building

Requires a C11 compiler, CMake, `libmosquitto`, and the .NET 8 SDK.

**C edge agent:**
```bash
cd edge-agent
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

**C# cloud bridge:**
```bash
cd cloud-bridge
dotnet build
```

## Running

Start a local MQTT broker (mosquitto, install via your package manager):
```bash
mosquitto -p 1883
```

In one terminal, start the dashboard. It runs until you stop it with
Ctrl+C; pass a fourth argument to stop after that many seconds instead:
```bash
cd cloud-bridge
dotnet run -- localhost 1883 "factory/demo/+/telemetry"        # until Ctrl+C
dotnet run -- localhost 1883 "factory/demo/+/telemetry" 30     # 30 seconds
```

In another terminal, run the edge agent (10 readings, no simulated fault):
```bash
cd edge-agent/build
./edge_agent 10
```

To see the fault-detection behavior, simulate a machine developing a fault
after reading 4:
```bash
./edge_agent 10 4
```

### Sample output

```
edge-agent: publishing 6 readings for 'press-01' to topic 'factory/demo/press-01/telemetry'
  [0] {"machine_id":"press-01","timestamp":1787093098,"temperature_c":41.60,"vibration_mm_s":2.00,"rpm":1444.0,"status":"ok"}
  ...
  [4] {"machine_id":"press-01","timestamp":1787093102,"temperature_c":41.60,"vibration_mm_s":3.80,"rpm":1450.0,"status":"fault"}
```

```
cloud-bridge: connecting to localhost:1883, subscribing to 'factory/demo/+/telemetry'
cloud-bridge: subscribed, waiting for telemetry...

  [press-01] temp= 41.60C  vibration= 2.00mm/s  rpm= 1444.0  status=ok
  [press-01] temp= 41.60C  vibration= 2.10mm/s  rpm= 1441.0  status=ok
  [press-01] temp= 42.00C  vibration= 2.20mm/s  rpm= 1441.0  status=ok
  [press-01] temp= 41.60C  vibration= 3.80mm/s  rpm= 1450.0  status=fault  <-- FAULT
  [press-01] temp= 42.00C  vibration= 5.30mm/s  rpm= 1444.0  status=fault  <-- FAULT

=== cloud-bridge summary ===
messages received : 6
fault readings     : 2
  press-01: 6 readings
```

## Running the tests

**C core logic** (no broker needed):
```bash
cd edge-agent/build
./edge_agent_tests
```

**C# MQTT protocol** (no broker needed — tests the wire-format functions
directly):
```bash
cd tests/MqttProtocolTests
dotnet run
```

## What's implemented vs. deliberately out of scope

Implemented: `CONNECT`/`CONNACK`, `SUBSCRIBE`/`SUBACK`, receiving
`PUBLISH` at any QoS, MQTT's variable-length "Remaining Length" encoding.

Not implemented: `PUBACK` (and the QoS 2 flow `PUBREC`/`PUBREL`/`PUBCOMP`),
`PINGREQ` keep-alive, clean `DISCONNECT`, and TLS. Two of these matter
for long runs:

- The client subscribes at QoS 1 but never sends `PUBACK`, so the broker
  keeps every delivered message "in flight". Once its in-flight limit is
  reached (20 by default in Mosquitto), delivery stalls.
- The client announces a 60 s keep-alive but never sends `PINGREQ`. If
  no other packet goes from client to broker, the broker closes the
  connection after 1.5 × keep-alive (90 s).

Neither affects the short CI run, but both would need fixing for a
long-running service. For anything beyond this demo, a full client
library (e.g. MQTTnet) is the right choice.

## Possible extensions

- Publish acknowledgment (`PUBACK`) so QoS 1 delivery is actually
  guaranteed rather than best-effort
- A minimal OPC UA variant alongside MQTT, since OPC UA is the other
  major industrial protocol for connecting PLCs and industrial PCs to
  the cloud
- Persist readings to a small database instead of printing them, and
  serve the dashboard over HTTP instead of the console
