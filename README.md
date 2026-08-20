# Edge-Cloud Bridge (C / C#)

A two-language demo mirroring the "device bridge" pattern common in
Industrial IoT: a **C edge agent** simulates a factory-floor sensor and
publishes telemetry over MQTT; a **C# cloud-side service**, with its MQTT
client hand-written over a raw TCP socket, subscribes and displays a live
dashboard. Built specifically to exercise the C/C# combination used in
industrial edge/cloud platforms, alongside the automotive-focused C++
projects elsewhere in this portfolio.

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
│   ├── Program.cs              → subscribes, parses JSON, prints a dashboard
│   └── NuGet.Config             → package sources cleared: no dependencies
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
whenever QoS > 0, and `test_edge_agent_core.c`'s
`"QoS 1 publish: packet identifier correctly skipped"` check pins the
regression down explicitly — it constructs a QoS 1 body by hand and
verifies the parsed payload matches exactly.

This is worth being upfront about in an interview: it's a real,
specific example of implementing a binary protocol from its
specification rather than a library, hitting an edge case the spec
mentions but that's easy to overlook, and fixing it with a test that
demonstrates the fix rather than just re-running the demo and eyeballing
the output.

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

In one terminal, start the dashboard (listens for 30 seconds by default when
given a duration argument, or indefinitely with none):
```bash
cd cloud-bridge
dotnet run -- localhost 1883 "dareto/demo/+/telemetry"
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
edge-agent: publishing 6 readings for 'press-01' to topic 'dareto/demo/press-01/telemetry'
  [0] {"machine_id":"press-01","timestamp":1787093098,"temperature_c":41.60,"vibration_mm_s":2.00,"rpm":1444.0,"status":"ok"}
  ...
  [4] {"machine_id":"press-01","timestamp":1787093102,"temperature_c":41.60,"vibration_mm_s":3.80,"rpm":1450.0,"status":"fault"}
```

```
cloud-bridge: connecting to localhost:1883, subscribing to 'dareto/demo/+/telemetry'
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

Not implemented, and not needed for what this project does: `PUBACK`/
`PUBREC`/`PUBREL`/`PUBCOMP` (the QoS 1/2 acknowledgment flows — the
broker doesn't require them from a subscriber that just wants to
receive), `PINGREQ` keep-alive, clean `DISCONNECT`, and TLS. A production
MQTT client would need all of these; this one implements exactly the
subset that this specific bridge exercises; a full client library
(e.g. MQTTnet) would be the right call for anything beyond a portfolio
demo.

## Possible extensions

- Publish acknowledgment (`PUBACK`) so QoS 1 delivery is actually
  guaranteed rather than best-effort
- A minimal OPC UA variant alongside MQTT, since OPC UA is the other
  major industrial protocol (used e.g. by Dareto's Device Bridge)
  connecting PLCs/industrial PCs to the cloud
- Persist readings to a small database instead of printing them, and
  serve the dashboard over HTTP instead of the console
