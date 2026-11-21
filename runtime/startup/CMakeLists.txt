enable_language(ASM_MASM)
# A link container for position-independent code and literals only. Build.ps1
# extracts its one section. This program is never launched or installed.
string(REPLACE "/RTC1" "" CMAKE_CXX_FLAGS_DEBUG "${CMAKE_CXX_FLAGS_DEBUG}")
add_executable(AwesomeStartup entries.asm startup.cpp)
target_compile_options(AwesomeStartup PRIVATE "$<$<COMPILE_LANGUAGE:CXX>:/GS-;/O1;/Oi;/Zl;/GR->")
target_link_options(AwesomeStartup PRIVATE /ENTRY:StartupTable /NODEFAULTLIB /MANIFEST:NO
    /MERGE:.rdata=.text /INCREMENTAL:NO /DEBUG:NONE /FIXED:NO /OPT:REF /OPT:ICF)
target_compile_options(AwesomeStartup PRIVATE "$<$<COMPILE_LANGUAGE:ASM_MASM>:/safeseh>")
