# MSVC's default Debug runtime checks add CRT startup and shutdown calls.
string(REPLACE "/RTC1" "" CMAKE_CXX_FLAGS_DEBUG "${CMAKE_CXX_FLAGS_DEBUG}")
add_library(AwesomeForTheXR32 SHARED src/marker.cpp src/exports.def)

# This diagnostic has no process-attach work or C runtime dependencies.
target_link_options(AwesomeForTheXR32 PRIVATE /NOENTRY /NODEFAULTLIB /INCREMENTAL:NO /Brepro)
target_link_options(AwesomeForTheXR32 PRIVATE "$<$<CONFIG:Release>:/DEBUG:NONE>")
