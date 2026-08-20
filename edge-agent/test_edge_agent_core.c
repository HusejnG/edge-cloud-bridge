/*
 * test_edge_agent_core.c
 *
 * Hand-rolled assert-based tests for edge_agent_core.c -- no external
 * test framework, consistent with the rest of this project's zero
 * external dependency approach. Same pattern as the C# side's test
 * runner: each check reports PASS/FAIL, the program exits non-zero if
 * anything failed, so it's usable in CI either way.
 */

#include "edge_agent_core.h"
#include <stdio.h>
#include <string.h>

static int g_passed = 0;
static int g_failed = 0;

#define CHECK(name, condition) do { \
    if (condition) { \
        g_passed++; \
        printf("[PASS] %s\n", name); \
    } else { \
        g_failed++; \
        printf("[FAIL] %s\n", name); \
    } \
} while (0)

static void test_build_payload_ok_status(void) {
    MachineState m = {"press-01", 41.6, 2.0, 1444.0, /*fault_after=*/-1};
    char buf[256];
    int len = build_payload(buf, sizeof(buf), &m, 1700000000L, /*reading_index=*/0);

    CHECK("build_payload: returns positive length", len > 0);
    CHECK("build_payload: contains machine_id",
          strstr(buf, "\"machine_id\":\"press-01\"") != NULL);
    CHECK("build_payload: status is ok when no fault configured",
          strstr(buf, "\"status\":\"ok\"") != NULL);
    CHECK("build_payload: temperature formatted to 2 decimal places",
          strstr(buf, "\"temperature_c\":41.60") != NULL);
}

static void test_build_payload_fault_status(void) {
    MachineState m = {"press-01", 41.6, 5.3, 1444.0, /*fault_after=*/3};
    char buf[256];
    build_payload(buf, sizeof(buf), &m, 1700000000L, /*reading_index=*/5);

    CHECK("build_payload: status is fault once reading_index >= fault_after",
          strstr(buf, "\"status\":\"fault\"") != NULL);
}

static void test_build_payload_fault_boundary(void) {
    MachineState m = {"press-01", 41.6, 2.0, 1444.0, /*fault_after=*/3};
    char buf[256];

    build_payload(buf, sizeof(buf), &m, 1700000000L, /*reading_index=*/2);
    CHECK("build_payload: still ok one reading before the fault threshold",
          strstr(buf, "\"status\":\"ok\"") != NULL);

    build_payload(buf, sizeof(buf), &m, 1700000000L, /*reading_index=*/3);
    CHECK("build_payload: fault exactly at the threshold reading",
          strstr(buf, "\"status\":\"fault\"") != NULL);
}

static void test_build_payload_truncation_safe(void) {
    MachineState m = {"press-01", 41.6, 2.0, 1444.0, -1};
    char tiny_buf[8];
    /* snprintf must never write past buf_size regardless of how small it
     * is -- this is the whole reason to use snprintf over sprintf. */
    int would_be_len = build_payload(tiny_buf, sizeof(tiny_buf), &m, 1700000000L, 0);

    CHECK("build_payload: reports the length it WOULD have written",
          would_be_len > (int)sizeof(tiny_buf));
    CHECK("build_payload: actually-written buffer is still null-terminated",
          tiny_buf[sizeof(tiny_buf) - 1] == '\0' || strlen(tiny_buf) < sizeof(tiny_buf));
}

static void test_step_reading_vibration_escalates_after_fault(void) {
    MachineState m = {"press-01", 40.0, 2.0, 1450.0, /*fault_after=*/2};

    step_reading(&m, 0);
    double vib_before_fault = m.vibration_mm_s;
    CHECK("step_reading: vibration stays low before fault threshold",
          vib_before_fault < 3.0);

    step_reading(&m, 2);
    double vib_at_fault = m.vibration_mm_s;
    step_reading(&m, 3);
    double vib_after_fault = m.vibration_mm_s;

    CHECK("step_reading: vibration escalates once past the fault threshold",
          vib_after_fault > vib_at_fault);
}

static void test_step_reading_no_fault_configured(void) {
    MachineState m = {"press-01", 40.0, 2.0, 1450.0, /*fault_after=*/-1};

    for (int i = 0; i < 20; ++i) {
        step_reading(&m, i);
    }

    CHECK("step_reading: vibration stays bounded when fault_after is -1 (disabled)",
          m.vibration_mm_s < 5.0);
}

int main(void) {
    test_build_payload_ok_status();
    test_build_payload_fault_status();
    test_build_payload_fault_boundary();
    test_build_payload_truncation_safe();
    test_step_reading_vibration_escalates_after_fault();
    test_step_reading_no_fault_configured();

    printf("\n%d/%d checks passed\n", g_passed, g_passed + g_failed);
    return g_failed == 0 ? 0 : 1;
}
