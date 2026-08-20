/*
 * edge_agent_core.h
 *
 * The parts of the edge agent that don't touch the network: building the
 * JSON telemetry payload and stepping the simulated machine state. Split
 * out from edge_agent_main.c specifically so this logic can be unit
 * tested without needing a running MQTT broker -- the broker-facing I/O
 * lives in edge_agent_main.c instead.
 */

#ifndef EDGE_AGENT_CORE_H
#define EDGE_AGENT_CORE_H

#include <stddef.h>

typedef struct {
    const char *machine_id;
    double temperature_c;
    double vibration_mm_s;
    double rpm;
    int fault_after_n_readings; /* simulate a fault after N publishes, -1 = never */
} MachineState;

/* Builds a compact JSON telemetry payload for one reading into buf.
 * Returns the number of bytes written (excluding the null terminator),
 * matching snprintf's convention. */
int build_payload(char *buf, size_t buf_size, const MachineState *m,
                   long timestamp, int reading_index);

/* Advances the simulated readings by one tick. Drifts vibration upward
 * (simulating a developing mechanical fault) once reading_index reaches
 * fault_after_n_readings. */
void step_reading(MachineState *m, int reading_index);

#endif /* EDGE_AGENT_CORE_H */
