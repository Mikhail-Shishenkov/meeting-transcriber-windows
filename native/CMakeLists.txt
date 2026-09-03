cmake_minimum_required(VERSION 3.21)
project(pmtwhisper-windows LANGUAGES CXX)

# whisper.cpp is fetched at a fixed commit (pinned v1.9.1).
# For the Windows MVP we want a self-contained pmtwhisper.dll, so the pinned
# whisper.cpp dependency is built as CPU-only static libraries:
#   - BUILD_SHARED_LIBS=OFF  -> static whisper/ggml libraries embedded in the DLL
#   - no WHISPER_CUDA/GGML_CUDA -> CPU backend only (MVP rule: CPU mode, no GPU)
include(FetchContent)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
FetchContent_Declare(
    whisper_cpp
    GIT_REPOSITORY https://github.com/ggml-org/whisper.cpp.git
    GIT_TAG f049fff95a089aa9969deb009cdd4892b3e74916
    GIT_SHALLOW FALSE
)
FetchContent_MakeAvailable(whisper_cpp)

if (NOT TARGET whisper)
    message(FATAL_ERROR "whisper target not found after FetchContent")
endif()

# pmtwhisper shared library exposing exactly the 10 C bridge functions.
add_library(pmtwhisper SHARED
    bridge/PolinWhisperBridge.cpp
)
target_link_libraries(pmtwhisper PRIVATE whisper)
target_compile_features(pmtwhisper PRIVATE cxx_std_17)
target_compile_definitions(pmtwhisper PRIVATE POLIN_WHISPER_RUNTIME_ENABLED=1)

# Deterministic runtime output location for every generator/config:
#   ${CMAKE_BINARY_DIR}/bin/pmtwhisper.dll
set_target_properties(pmtwhisper PROPERTIES
    OUTPUT_NAME pmtwhisper
    PREFIX ""
    POSITION_INDEPENDENT_CODE ON
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin"
)

# Windows: the exact export set is controlled by a module-definition file
# (bridge/pmtwhisper.def); the CMake/MSVC wiring below hands it to the
# linker via /DEF. No per-symbol __declspec(dllexport) is required.
if (WIN32)
    target_sources(pmtwhisper PRIVATE bridge/pmtwhisper.def)
endif()
