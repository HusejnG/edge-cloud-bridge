cmake_minimum_required(VERSION 3.16)
project(edge_agent C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

find_package(PkgConfig REQUIRED)
pkg_check_modules(MOSQUITTO REQUIRED libmosquitto)

# ---------------------------------------------------------------------------
# Main executable
# ---------------------------------------------------------------------------
add_executable(edge_agent edge_agent_main.c edge_agent_core.c)
target_include_directories(edge_agent PRIVATE ${MOSQUITTO_INCLUDE_DIRS})
target_link_libraries(edge_agent PRIVATE ${MOSQUITTO_LIBRARIES})
target_compile_options(edge_agent PRIVATE -Wall -Wextra)

# ---------------------------------------------------------------------------
# Tests -- core logic only, no broker/network needed
# ---------------------------------------------------------------------------
add_executable(edge_agent_tests test_edge_agent_core.c edge_agent_core.c)
target_compile_options(edge_agent_tests PRIVATE -Wall -Wextra)

enable_testing()
add_test(NAME edge_agent_core_tests COMMAND edge_agent_tests)
