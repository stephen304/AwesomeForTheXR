cmake_minimum_required(VERSION 3.20)
project(AwesomeForTheXRRuntime LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "Configure with an MSVC Win32 toolchain for the 32-bit Unity player.")
endif()

add_subdirectory(plugin)
add_subdirectory(forwarder)
add_subdirectory(startup)
