# All managed dependencies and the private runtime are optional. A default
# Apollo build does not discover .NET, download the SDK or ship this component.
find_package(Python3 3.11 REQUIRED COMPONENTS Interpreter)
find_program(DOTNET_EXECUTABLE NAMES dotnet REQUIRED)

set(DUALSENSE_EDGE_COMPONENT "${CMAKE_BINARY_DIR}/controller")
set(DUALSENSE_EDGE_WORK "${CMAKE_BINARY_DIR}/dualsense-edge")
file(GLOB_RECURSE DUALSENSE_EDGE_SOURCES CONFIGURE_DEPENDS
        "${CMAKE_SOURCE_DIR}/src/platform/windows/dualsense_edge/*.cs"
        "${CMAKE_SOURCE_DIR}/src/platform/windows/dualsense_edge/*.csproj"
        "${CMAKE_SOURCE_DIR}/src/platform/windows/dualsense_edge/*.json"
        "${CMAKE_SOURCE_DIR}/third-party/hidmaestro/*")

add_custom_command(
        OUTPUT "${DUALSENSE_EDGE_COMPONENT}/component-build.json"
        COMMAND "${Python3_EXECUTABLE}" "${CMAKE_SOURCE_DIR}/tools/dualsense-edge/build.py"
                --work "${DUALSENSE_EDGE_WORK}" --output "${DUALSENSE_EDGE_COMPONENT}"
                --dotnet "${DOTNET_EXECUTABLE}"
        DEPENDS ${DUALSENSE_EDGE_SOURCES} "${CMAKE_SOURCE_DIR}/tools/dualsense-edge/build.py"
        COMMENT "Building the DualSense Edge controller component"
        VERBATIM)
add_custom_target(dualsense-edge-component DEPENDS "${DUALSENSE_EDGE_COMPONENT}/component-build.json")
add_dependencies(sunshine dualsense-edge-component)

# Explicitly invoked diagnostics; never creates a controller during a build.
add_executable(dualsense-edge-probe EXCLUDE_FROM_ALL
        "${CMAKE_SOURCE_DIR}/tools/dualsense-edge/probe.cpp"
        "${CMAKE_SOURCE_DIR}/src/platform/windows/dualsense_edge.cpp")
target_link_libraries(dualsense-edge-probe ${Boost_LIBRARIES} ws2_32)
target_compile_definitions(dualsense-edge-probe PRIVATE ${SUNSHINE_DEFINITIONS})
target_compile_options(dualsense-edge-probe PRIVATE ${SUNSHINE_COMPILE_OPTIONS})
set_target_properties(dualsense-edge-probe PROPERTIES CXX_STANDARD 23)
add_dependencies(dualsense-edge-probe dualsense-edge-component)

add_custom_target(dualsense-edge-tests
        COMMAND "${Python3_EXECUTABLE}" "${CMAKE_SOURCE_DIR}/tools/dualsense-edge/build.py"
                --work "${DUALSENSE_EDGE_WORK}" --output "${DUALSENSE_EDGE_COMPONENT}"
                --dotnet "${DOTNET_EXECUTABLE}" --test
        DEPENDS dualsense-edge-component
        COMMENT "Testing DualSense Edge reports without creating a device"
        VERBATIM)

install(DIRECTORY "${DUALSENSE_EDGE_COMPONENT}/" DESTINATION "controller" COMPONENT application
        PATTERN "*.pdb" EXCLUDE)
