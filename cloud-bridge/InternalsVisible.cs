// Grants the test project access to internal MQTT protocol functions
// (variable-length encoding, PUBLISH parsing) without making them part
// of the public API surface.
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("MqttProtocolTests")]
