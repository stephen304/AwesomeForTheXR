# Only the test runner sees this substitute loader. The patch packages always
# take the Khronos DLL from the plugin build directory.
add_library(PreflightFixture SHARED fixture.cpp stereo_fixture.cpp fixture.def)
set_target_properties(PreflightFixture PROPERTIES OUTPUT_NAME openxr_loader)
target_include_directories(PreflightFixture PRIVATE "${openxr_package_SOURCE_DIR}/include")

add_executable(PreflightTests tests.cpp)
target_include_directories(PreflightTests PRIVATE "${openxr_package_SOURCE_DIR}/include")
target_link_libraries(PreflightTests PRIVATE dxgi d3d11)
target_compile_features(PreflightTests PRIVATE cxx_std_17)
add_dependencies(PreflightTests PreflightFixture AwesomeForTheXR32)

foreach(scenario IN ITEMS success destroy-failure late-headset no-headset unsupported-headset no-runtime
        no-extension no-api runtime-properties system-properties lookup null-entry requirements
        device-match device-wrong-adapter device-low-level device-null device-single-thread stereo-resume-exit stereo-loss)
    add_test(NAME preflight.${scenario}
        COMMAND PreflightTests $<TARGET_FILE:AwesomeForTheXR32> ${scenario})
    # The production plugin writes its log next to the process executable.
    set_tests_properties(preflight.${scenario} PROPERTIES RESOURCE_LOCK preflight_log TIMEOUT 45)
endforeach()