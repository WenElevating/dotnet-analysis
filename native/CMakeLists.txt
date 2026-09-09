cmake_minimum_required(VERSION 3.25)

project(DotnetAnalysisRetentionProfiler LANGUAGES CXX)

include(CTest)

# 该目录仅构建 Windows AMD64 的 CLR Profiler Controller；Profiler DLL 会在后续目标中复用同一协议。
if(NOT WIN32)
    message(FATAL_ERROR "DotnetAnalysisRetentionProfiler only supports Windows.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "DotnetAnalysisRetentionProfiler only supports AMD64.")
endif()

add_library(DotnetAnalysisRetentionProfilerController SHARED
    controller/RetentionProfilerController.cpp)
set_property(TARGET DotnetAnalysisRetentionProfilerController PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded")

target_compile_features(DotnetAnalysisRetentionProfilerController PRIVATE cxx_std_20)
target_compile_definitions(DotnetAnalysisRetentionProfilerController PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_compile_options(DotnetAnalysisRetentionProfilerController PRIVATE /W4 /WX /permissive- /utf-8)
target_link_libraries(DotnetAnalysisRetentionProfilerController PRIVATE ole32)
set_target_properties(DotnetAnalysisRetentionProfilerController PROPERTIES
    OUTPUT_NAME "DotnetAnalysis.RetentionProfilerController")

find_path(DotnetAnalysisCorProfIncludeDirectory corprof.h
    PATHS
        "C:/Program Files (x86)/Windows Kits/NETFXSDK/4.8.1/Include/um"
        "C:/Program Files (x86)/Windows Kits/NETFXSDK/4.8/Include/um"
    REQUIRED)

target_include_directories(DotnetAnalysisRetentionProfilerController PRIVATE
    ${DotnetAnalysisCorProfIncludeDirectory})
target_link_libraries(DotnetAnalysisRetentionProfilerController PRIVATE mscoree)

add_library(DotnetAnalysisRetentionProfiler SHARED
    profiler/RetentionProfiler.cpp)
set_property(TARGET DotnetAnalysisRetentionProfiler PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded")
target_compile_features(DotnetAnalysisRetentionProfiler PRIVATE cxx_std_20)
target_compile_definitions(DotnetAnalysisRetentionProfiler PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_compile_options(DotnetAnalysisRetentionProfiler PRIVATE /W4 /WX /permissive- /utf-8)
target_include_directories(DotnetAnalysisRetentionProfiler PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${DotnetAnalysisCorProfIncludeDirectory})
target_link_options(DotnetAnalysisRetentionProfiler PRIVATE
    "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/profiler/RetentionProfiler.def")
set_target_properties(DotnetAnalysisRetentionProfiler PROPERTIES
    OUTPUT_NAME "DotnetAnalysis.RetentionProfiler")

if(BUILD_TESTING)
    add_executable(DotnetAnalysisRetentionProfilerProtocolTests
        tests/RetentionProfilerProtocolTests.cpp)
    target_compile_features(DotnetAnalysisRetentionProfilerProtocolTests PRIVATE cxx_std_20)
    target_compile_definitions(DotnetAnalysisRetentionProfilerProtocolTests PRIVATE
        UNICODE
        _UNICODE
        WIN32_LEAN_AND_MEAN
        NOMINMAX)
    target_compile_options(DotnetAnalysisRetentionProfilerProtocolTests PRIVATE /W4 /WX /permissive- /utf-8)
    target_include_directories(DotnetAnalysisRetentionProfilerProtocolTests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    add_test(NAME DotnetAnalysisRetentionProfilerProtocolTests COMMAND DotnetAnalysisRetentionProfilerProtocolTests)

    add_executable(DotnetAnalysisRetentionProfilerComExportTests
        tests/RetentionProfilerComExportTests.cpp)
    target_compile_features(DotnetAnalysisRetentionProfilerComExportTests PRIVATE cxx_std_20)
    target_compile_definitions(DotnetAnalysisRetentionProfilerComExportTests PRIVATE
        UNICODE
        _UNICODE
        WIN32_LEAN_AND_MEAN
        NOMINMAX)
    target_compile_options(DotnetAnalysisRetentionProfilerComExportTests PRIVATE /W4 /WX /permissive- /utf-8)
    target_include_directories(DotnetAnalysisRetentionProfilerComExportTests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${DotnetAnalysisCorProfIncludeDirectory})
    add_test(NAME DotnetAnalysisRetentionProfilerComExportTests
        COMMAND DotnetAnalysisRetentionProfilerComExportTests $<TARGET_FILE:DotnetAnalysisRetentionProfiler>)
endif()
