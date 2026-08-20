/*
 * edge_agent_core.c
 *
 * See edge_agent_core.h. Deliberately hand-builds JSON via snprintf
 * rather than pulling in a JSON library -- for a handful of flat numeric
 * fields, on an edge device, that's proportionate; a library would be
 * pure overhead.
 */

#include "edge_agent_core.h"
#include <stdio.h>

int build_payload(char *buf, size_t buf_size, const MachineState *m,
                   long timestamp, int reading_index) {
    const char *status = "ok";
    if (m->fault_after_n_readings >= 0 && reading_index >= m->fault_after_n_readings) {
        status = "fault";
    }
    return snprintf(buf, buf_size,
        "{\"machine_id\":\"%s\",\"timestamp\":%ld,\"temperature_c\":%.2f,"
        "\"vibration_mm_s\":%.2f,\"rpm\":%.1f,\"status\":\"%s\"}",
        m->machine_id, timestamp, m->temperature_c, m->vibration_mm_s,
        m->rpm, status);
}

void step_reading(MachineState *m, int reading_index) {
    m->temperature_c += ((reading_index % 3) - 1) * 0.4;   /* wanders +/- */
    m->rpm += ((reading_index % 5) - 2) * 3.0;

    if (m->fault_after_n_readings >= 0 && reading_index >= m->fault_after_n_readings) {
        m->vibration_mm_s += 1.5; /* escalating vibration = developing fault */
    } else {
        m->vibration_mm_s = 2.0 + (reading_index % 4) * 0.1;
    }
}
