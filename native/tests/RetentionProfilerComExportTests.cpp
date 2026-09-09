#include "profiler/RetentionProfilerProtocol.h"

#include <objbase.h>
#include <cor.h>
#include <corprof.h>

#include <cassert>

/// <summary>
/// 验证构建产物实际导出 DllGetClassObject，且只为保留分析 CLSID 创建 Callback3 类工厂。
/// </summary>
int wmain(int argc, wchar_t* argv[])
{
    assert(argc == 2);
    const HMODULE module = LoadLibraryW(argv[1]);
    assert(module != nullptr);
    using DllGetClassObjectFunction = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
    const auto getClassObject = reinterpret_cast<DllGetClassObjectFunction>(GetProcAddress(module, "DllGetClassObject"));
    assert(getClassObject != nullptr);

    IClassFactory* factory = nullptr;
    const HRESULT result = getClassObject(
        DotnetAnalysis::kRetentionProfilerClsid,
        IID_IClassFactory,
        reinterpret_cast<void**>(&factory));
    assert(SUCCEEDED(result));
    assert(factory != nullptr);
    ICorProfilerCallback2* callback2 = nullptr;
    const HRESULT createResult = factory->CreateInstance(nullptr, __uuidof(ICorProfilerCallback2), reinterpret_cast<void**>(&callback2));
    assert(SUCCEEDED(createResult));
    assert(callback2 != nullptr);
    ICorProfilerCallback3* callback3 = nullptr;
    const HRESULT callback3Result = callback2->QueryInterface(__uuidof(ICorProfilerCallback3), reinterpret_cast<void**>(&callback3));
    assert(SUCCEEDED(callback3Result));
    assert(callback3 != nullptr);
    callback3->Release();
    callback2->Release();
    factory->Release();
    FreeLibrary(module);
    return 0;
}
