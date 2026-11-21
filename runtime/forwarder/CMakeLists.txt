add_executable(AwesomeDirectForwarder WIN32 forwarder.cpp)
set_target_properties(AwesomeDirectForwarder PROPERTIES
    OUTPUT_NAME Awesome_DirectToRift
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_link_options(AwesomeDirectForwarder PRIVATE /INCREMENTAL:NO /Brepro "$<$<CONFIG:Release>:/DEBUG:NONE>")
