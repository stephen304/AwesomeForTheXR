include(../cmake/OpenXRPackage.cmake)
add_library(AwesomeForTheXR32 SHARED src/preflight.cpp src/stereo.cpp src/exports.def)
# Compile the two shaders into the DLL. The installed game needs no shader compiler.
get_filename_component(kits_root "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Kits\\Installed Roots;KitsRoot10]" ABSOLUTE)
find_program(fxc NAMES fxc.exe HINTS "${kits_root}/bin/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/x64" REQUIRED)
foreach(entry IN ITEMS Fullscreen FlipEye)
    if(entry STREQUAL "Fullscreen")
        set(profile vs_4_0)
    else()
        set(profile ps_4_0)
    endif()
    set(header "${CMAKE_CURRENT_BINARY_DIR}/${entry}.h")
    add_custom_command(OUTPUT "${header}"
        COMMAND "${fxc}" /nologo /O3 /T ${profile} /E ${entry} /Vn ${entry}Code /Fh "${header}" "${CMAKE_CURRENT_SOURCE_DIR}/src/eye_copy.hlsl"
        DEPENDS src/eye_copy.hlsl VERBATIM)
    target_sources(AwesomeForTheXR32 PRIVATE "${header}")
endforeach()
target_include_directories(AwesomeForTheXR32 PRIVATE "${CMAKE_CURRENT_BINARY_DIR}")
target_link_libraries(AwesomeForTheXR32 PRIVATE user32 dxgi)
target_compile_features(AwesomeForTheXR32 PRIVATE cxx_std_17)
target_include_directories(AwesomeForTheXR32 PRIVATE "${openxr_package_SOURCE_DIR}/include")
set_target_properties(AwesomeForTheXR32 PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_link_options(AwesomeForTheXR32 PRIVATE /INCREMENTAL:NO /Brepro "$<$<CONFIG:Release>:/DEBUG:NONE>")

# The same pinned loader is packaged with all configurations.
configure_file("${loader_dir}/bin/openxr_loader.dll" "${CMAKE_CURRENT_BINARY_DIR}/openxr_loader.dll" COPYONLY)
if(AFTXR_BUILD_TESTS)
    add_subdirectory(tests)
endif()
