#include "RetentionProfilerController.h"
#include "../profiler/RetentionProfilerProtocol.h"

#include <metahost.h>

/// <summary>
/// 附加指定 Profiler 并保持接口释放、COM 初始化与调用方资源所有权完全在 Controller 内闭合。
/// </summary>
HRESULT __stdcall DotnetAnalysisAttachRetentionProfiler(
    DWORD processId,
    DWORD timeoutMilliseconds,
    const CLSID* profilerClsid,
    const wchar_t* profilerPath,
    const void* clientData,
    DWORD clientDataLength)
{
    if (processId == 0 || profilerClsid == nullptr || profilerPath == nullptr || profilerPath[0] == L'\0')
    {
        return E_INVALIDARG;
    }

    if (clientData == nullptr
        || clientDataLength != sizeof(DotnetAnalysis::RetentionProfilerAttachData)
        || !DotnetAnalysis::IsValidRetentionProfilerAttachData(
            *static_cast<const DotnetAnalysis::RetentionProfilerAttachData*>(clientData)))
    {
        return E_INVALIDARG;
    }

    ICLRProfiling* profiling = nullptr;
    const HRESULT createResult = CLRCreateInstance(
        CLSID_CLRProfiling,
        IID_ICLRProfiling,
        reinterpret_cast<void**>(&profiling));
    if (FAILED(createResult))
    {
        return createResult;
    }

    const HRESULT attachResult = profiling->AttachProfiler(
        processId,
        timeoutMilliseconds,
        profilerClsid,
        profilerPath,
        const_cast<void*>(clientData),
        clientDataLength);
    profiling->Release();
    return attachResult;
}
