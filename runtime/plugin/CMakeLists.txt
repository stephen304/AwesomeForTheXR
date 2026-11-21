include(../cmake/OpenXRPackage.cmake)
add_library(AwesomeForTheXR32 SHARED src/marker.cpp src/preflight.cpp src/exports.def)
target_link_libraries(AwesomeForTheXR32 PRIVATE user32)
target_compile_features(AwesomeForTheXR32 PRIVATE cxx_std_17)
target_include_directories(AwesomeForTheXR32 PRIVATE "${openxr_package_SOURCE_DIR}/include")
set_target_properties(AwesomeForTheXR32 PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_link_options(AwesomeForTheXR32 PRIVATE /INCREMENTAL:NO /Brepro "$<$<CONFIG:Release>:/DEBUG:NONE>")

# The same pinned loader is packaged with all configurations.
configure_file("${loader_dir}/bin/openxr_loader.dll" "${CMAKE_CURRENT_BINARY_DIR}/openxr_loader.dll" COPYONLY)
if(AFTXR_BUILD_TESTS)
    add_subdirectory(tests)
endif()
