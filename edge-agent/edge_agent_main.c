/*
 * edge_agent_main.c
 *
 * The MQTT/network-facing part of the edge agent: connects to the
 * broker and publishes readings produced by edge_agent_core. See
 * edge_agent_core.h for the testable simulation/encoding logic this
 * builds on.
 */

#include "edge_agent_core.h"
#include <mosquitto.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#define BROKER_HOST "localhost"
#define BROKER_PORT 1883
#define TOPIC_PREFIX "dareto/demo/"
#define PUBLISH_INTERVAL_SEC 1

int main(int argc, char **argv) {
    int reading_count = (argc > 1) ? atoi(argv[1]) : 10;
    int fault_after = (argc > 2) ? atoi(argv[2]) : -1;

    MachineState machine = {
        .machine_id = "press-01",
        .temperature_c = 42.0,
        .vibration_mm_s = 2.0,
        .rpm = 1450.0,
        .fault_after_n_readings = fault_after,
    };

    mosquitto_lib_init();

    struct mosquitto *mosq = mosquitto_new(NULL, true, NULL);
    if (!mosq) {
        fprintf(stderr, "Failed to create mosquitto client\n");
        return 1;
    }

    int rc = mosquitto_connect(mosq, BROKER_HOST, BROKER_PORT, 30);
    if (rc != MOSQ_ERR_SUCCESS) {
        fprintf(stderr, "Could not connect to broker at %s:%d (%s)\n",
                BROKER_HOST, BROKER_PORT, mosquitto_strerror(rc));
        mosquitto_destroy(mosq);
        mosquitto_lib_cleanup();
        return 1;
    }

    char topic[128];
    snprintf(topic, sizeof(topic), TOPIC_PREFIX "%s/telemetry", machine.machine_id);

    fprintf(stderr, "edge-agent: publishing %d readings for '%s' to topic '%s'\n",
            reading_count, machine.machine_id, topic);

    for (int i = 0; i < reading_count; ++i) {
        step_reading(&machine, i);

        char payload[256];
        int len = build_payload(payload, sizeof(payload), &machine, (long)time(NULL), i);

        rc = mosquitto_publish(mosq, NULL, topic, len, payload, /*qos=*/1, /*retain=*/false);
        if (rc != MOSQ_ERR_SUCCESS) {
            fprintf(stderr, "publish failed: %s\n", mosquitto_strerror(rc));
        } else {
            fprintf(stderr, "  [%d] %s\n", i, payload);
        }

        mosquitto_loop(mosq, 100, 1);
        if (i < reading_count - 1) {
            sleep(PUBLISH_INTERVAL_SEC);
        }
    }

    mosquitto_disconnect(mosq);
    mosquitto_destroy(mosq);
    mosquitto_lib_cleanup();
    return 0;
}
