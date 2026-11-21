if(POLICY CMP0135)
    cmake_policy(SET CMP0135 NEW)
endif()
include(FetchContent)
FetchContent_Declare(openxr_package
    URL https://github.com/KhronosGroup/OpenXR-SDK/releases/download/release-1.1.65/OpenXR.Loader.1.1.65.nupkg
    URL_HASH SHA256=05638c526f07ba3a5f1084bbc3308d654d5167f844ffa1e35515325cb72e0c34
    DOWNLOAD_NAME openxr-loader.zip
)
FetchContent_MakeAvailable(openxr_package)
set(loader_dir "${openxr_package_SOURCE_DIR}/native/Win32/release")
