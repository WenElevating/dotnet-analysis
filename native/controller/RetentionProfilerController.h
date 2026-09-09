#pragma once

#include <windows.h>

/// <summary>
/// Controller 导出的 CLR Profiler 附加入口；调用方拥有 profilerPath 与 clientData 的生命周期直到函数返回。
/// </summary>
/// <param name="processId">目标 CoreCLR 进程 PID。</param>
/// <param name="timeoutMilliseconds">CLR 附加握手最长等待时间。</param>
/// <param name="profilerClsid">Profiler COM 类标识。</param>
/// <param name="profilerPath">Profiler DLL 的绝对路径。</param>
/// <param name="clientData">传给 InitializeForAttach 的协议数据。</param>
/// <param name="clientDataLength">协议数据字节数。</param>
/// <returns>CLR AttachProfiler 的 HRESULT；成功时为 S_OK。</returns>
extern "C" __declspec(dllexport) HRESULT __stdcall DotnetAnalysisAttachRetentionProfiler(
    DWORD processId,
    DWORD timeoutMilliseconds,
    const CLSID* profilerClsid,
    const wchar_t* profilerPath,
    const void* clientData,
    DWORD clientDataLength);
