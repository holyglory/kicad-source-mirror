cmake_minimum_required(VERSION 3.21)
project(kicad_windows_json_boundary LANGUAGES CXX)
if(NOT MSVC)
    message(FATAL_ERROR "This fixture requires the real Windows MSVC DLL linker")
endif()
get_filename_component(KICAD_SOURCE "${CMAKE_CURRENT_LIST_DIR}/../../../.." ABSOLUTE)
add_library(json_provider SHARED provider.cpp)
target_compile_definitions(json_provider PRIVATE KICOMMON_DLL)
target_include_directories(json_provider PUBLIC "${KICAD_SOURCE}/include" "${KICAD_SOURCE}/thirdparty/nlohmann_json")
target_compile_features(json_provider PUBLIC cxx_std_20)
target_compile_options(json_provider PRIVATE /Ob0)
foreach(kind IN ITEMS wrapped raw)
    add_executable(${kind} EXCLUDE_FROM_ALL consumer.cpp imported.cpp)
    target_link_libraries(${kind} PRIVATE json_provider)
    target_compile_options(${kind} PRIVATE /Ob0)
endforeach()
target_compile_definitions(wrapped PRIVATE USE_KICAD_JSON_IMPORTS)
add_executable(missing_provider EXCLUDE_FROM_ALL consumer.cpp imported.cpp)
target_include_directories(missing_provider PRIVATE "${KICAD_SOURCE}/include" "${KICAD_SOURCE}/thirdparty/nlohmann_json")
target_compile_features(missing_provider PRIVATE cxx_std_20)
target_compile_definitions(missing_provider PRIVATE USE_KICAD_JSON_IMPORTS)
target_compile_options(missing_provider PRIVATE /Ob0)

# Compile the production Windows-specific translation unit against the same
# DLL import boundary, not only a generic JSON consumer.
add_executable(native_request EXCLUDE_FROM_ALL
    "${KICAD_SOURCE}/kicad/automation_update_windows.cpp"
    "${KICAD_SOURCE}/automation/tests/fixtures/windows-restart-request.cpp"
    imported.cpp)
target_include_directories(native_request PRIVATE "${KICAD_SOURCE}/kicad")
target_link_libraries(native_request PRIVATE json_provider)
target_compile_options(native_request PRIVATE /Ob0 /utf-8)

# Deliberately restore the observed bad include in a disposable build copy.
# The real source is unchanged; this control must fail specifically at the DLL link.
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/raw_native")
file(READ "${KICAD_SOURCE}/kicad/automation_update_windows.h" raw_header)
string(REPLACE "#include <json_common.h>" "#include <nlohmann/json.hpp>" raw_header "${raw_header}")
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/raw_native/automation_update_windows.h" "${raw_header}")
configure_file("${KICAD_SOURCE}/kicad/automation_update_windows.cpp"
    "${CMAKE_CURRENT_BINARY_DIR}/raw_native/automation_update_windows.cpp" COPYONLY)
add_executable(raw_native_request EXCLUDE_FROM_ALL
    "${CMAKE_CURRENT_BINARY_DIR}/raw_native/automation_update_windows.cpp"
    "${KICAD_SOURCE}/automation/tests/fixtures/windows-restart-request.cpp"
    imported.cpp)
target_include_directories(raw_native_request PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/raw_native")
target_link_libraries(raw_native_request PRIVATE json_provider)
target_compile_options(raw_native_request PRIVATE /Ob0 /utf-8)
