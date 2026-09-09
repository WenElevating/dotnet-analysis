#include "RetentionProfilerProtocol.h"

#include <cor.h>
#include <corprof.h>
#include <strsafe.h>

#include <atomic>
#include <new>

namespace
{
    constexpr std::size_t kCallback3VtableLength = 83;
    constexpr std::size_t kInitializeSlot = 3;
    constexpr std::size_t kShutdownSlot = 4;
    constexpr std::size_t kObjectReferencesSlot = 52;
    constexpr std::size_t kGarbageCollectionStartedSlot = 73;
    constexpr std::size_t kGarbageCollectionFinishedSlot = 75;
    constexpr std::size_t kRootReferences2Slot = 77;
    constexpr std::size_t kInitializeForAttachSlot = 80;
    constexpr std::size_t kProfilerAttachCompleteSlot = 81;
    constexpr std::size_t kProfilerDetachSucceededSlot = 82;
    constexpr std::uint32_t kMaximumFunctionEvidence = 1024;
    constexpr std::size_t kTypeHashCapacity = 8192;
    constexpr std::size_t kMaximumTypeResolutionDepth = 8;
    std::atomic<LONG> s_activeProfilerObjects{ 0 };
    std::atomic<LONG> s_activeClassFactories{ 0 };
    std::atomic<LONG> s_serverLocks{ 0 };

    /// <summary>
    /// 保存一个附加会话的所有非托管资源；对象由 CLR 以 ICorProfilerCallback3 COM ABI 调用。
    /// </summary>
    struct RawRetentionProfiler final
    {
        void** vtable;
        std::atomic<ULONG> referenceCount;
        ICorProfilerInfo3* profilerInfo;
        ICorProfilerInfo3* forceGcProfilerInfo;
        HANDLE mappingHandle;
        HANDLE completionEvent;
        HANDLE failureEvent;
        HANDLE detachEvent;
        DotnetAnalysis::RetentionProfilerSharedHeader* header;
        bool overflowed;
        UINT_PTR typeHashKeys[kTypeHashCapacity];
        std::uint32_t typeHashEvidenceIndexes[kTypeHashCapacity];
    };

    /// <summary>
    /// 以 AMD64 COM ABI 接收未启用的 Callback 槽位；所有参数由调用方传入但不被访问。
    /// </summary>
    HRESULT STDMETHODCALLTYPE NoopCallback(void*)
    {
        return S_OK;
    }

    /// <summary>
    /// 在不依赖外部 C++ Profiler 基类的情况下将精确回调函数放入原始 COM 槽位。
    /// </summary>
    template <typename TFunction>
    void* GetVtableSlot(TFunction function)
    {
        union FunctionPointer final
        {
            TFunction typed;
            void* untyped;
        } value{ function };
        return value.untyped;
    }

    /// <summary>
    /// 释放已打开的 Controller 资源和 CLR 信息接口；可从 Shutdown、Detach 或 Release 调用。
    /// </summary>
    void CloseResources(RawRetentionProfiler* profiler)
    {
        if (profiler->header != nullptr)
        {
            UnmapViewOfFile(profiler->header);
            profiler->header = nullptr;
        }
        if (profiler->mappingHandle != nullptr)
        {
            CloseHandle(profiler->mappingHandle);
            profiler->mappingHandle = nullptr;
        }
        if (profiler->completionEvent != nullptr)
        {
            CloseHandle(profiler->completionEvent);
            profiler->completionEvent = nullptr;
        }
        if (profiler->failureEvent != nullptr)
        {
            CloseHandle(profiler->failureEvent);
            profiler->failureEvent = nullptr;
        }
        if (profiler->detachEvent != nullptr)
        {
            CloseHandle(profiler->detachEvent);
            profiler->detachEvent = nullptr;
        }
        if (profiler->profilerInfo != nullptr)
        {
            profiler->profilerInfo->Release();
            profiler->profilerInfo = nullptr;
        }
    }

    /// <summary>
    /// 将协议失败状态发布给 Controller；失败后任何后续回调都不再写入记录区。
    /// </summary>
    void FailCapture(RawRetentionProfiler* profiler, HRESULT failureHResult = E_FAIL)
    {
        profiler->overflowed = true;
        if (profiler->header != nullptr)
        {
            InterlockedCompareExchange(&profiler->header->failureHResult, failureHResult, S_OK);
            InterlockedExchange(&profiler->header->status, static_cast<LONG>(DotnetAnalysis::RetentionProfilerCaptureStatus::Failed));
        }
        if (profiler->failureEvent != nullptr)
        {
            SetEvent(profiler->failureEvent);
        }
    }

    /// <summary>
    /// 校验共享映射区段完全位于声明容量内，避免任何回调写到映射外。
    /// </summary>
    bool IsRegionValid(std::uint32_t offset, std::uint32_t count, std::size_t itemSize, std::uint32_t totalBytes)
    {
        const auto bytes = static_cast<std::uint64_t>(count) * itemSize;
        return offset >= sizeof(DotnetAnalysis::RetentionProfilerSharedHeader)
            && static_cast<std::uint64_t>(offset) + bytes <= totalBytes;
    }

    /// <summary>
    /// 原子预留记录区中的一个槽位；容量不足时冻结为失败，不会越界写入。
    /// </summary>
    LONG ReserveSlot(RawRetentionProfiler* profiler, volatile LONG* count, std::uint32_t capacity)
    {
        const LONG slot = InterlockedIncrement(count) - 1;
        if (slot < 0 || static_cast<std::uint32_t>(slot) >= capacity)
        {
            FailCapture(profiler, E_OUTOFMEMORY);
            return -1;
        }

        InterlockedExchange64(&profiler->header->lastProgressTickCount, GetTickCount64());
        return slot;
    }

    /// <summary>
    /// 实现 COM 查询；仅声称 IUnknown 与 Callback1/2/3，确保 CLR 不会调用未声明的后续接口。
    /// </summary>
    HRESULT STDMETHODCALLTYPE QueryInterface(RawRetentionProfiler* profiler, REFIID riid, void** result)
    {
        if (result == nullptr)
        {
            return E_POINTER;
        }
        *result = nullptr;
        if (riid == IID_IUnknown
            || riid == __uuidof(ICorProfilerCallback)
            || riid == __uuidof(ICorProfilerCallback2)
            || riid == __uuidof(ICorProfilerCallback3))
        {
            *result = profiler;
            ++profiler->referenceCount;
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    /// <summary>
    /// 增加 Profiler COM 引用计数。
    /// </summary>
    ULONG STDMETHODCALLTYPE AddRef(RawRetentionProfiler* profiler)
    {
        return ++profiler->referenceCount;
    }

    /// <summary>
    /// 释放 Profiler COM 引用，并在最后一个引用消失时释放非托管资源。
    /// </summary>
    ULONG STDMETHODCALLTYPE Release(RawRetentionProfiler* profiler)
    {
        const ULONG remaining = --profiler->referenceCount;
        if (remaining == 0)
        {
            CloseResources(profiler);
            --s_activeProfilerObjects;
            delete profiler;
        }
        return remaining;
    }

    /// <summary>
    /// 正常启动路径不支持；本组件只允许受控 AttachProfiler 流程调用 InitializeForAttach。
    /// </summary>
    HRESULT STDMETHODCALLTYPE Initialize(RawRetentionProfiler*)
    {
        return E_NOTIMPL;
    }

    /// <summary>
    /// CLR 关闭时释放所有句柄，避免目标进程退出后残留命名对象引用。
    /// </summary>
    HRESULT STDMETHODCALLTYPE Shutdown(RawRetentionProfiler* profiler)
    {
        CloseResources(profiler);
        return S_OK;
    }

    /// <summary>
    /// 在 CLR Profiler 回调之外的原生线程上请求一次 GC；ForceGC 不允许从 ProfilerAttachComplete 回调线程直接调用。
    /// </summary>
    DWORD WINAPI ForceGarbageCollectionOnNativeThread(void* parameter)
    {
        auto* profiler = static_cast<RawRetentionProfiler*>(parameter);
        auto* profilerInfo = profiler->forceGcProfilerInfo;
        if (profilerInfo == nullptr)
        {
            FailCapture(profiler, E_POINTER);
        }
        else
        {
            const HRESULT forceGcResult = profilerInfo->ForceGC();
            if (FAILED(forceGcResult))
            {
                FailCapture(profiler, forceGcResult);
            }
            profilerInfo->Release();
            profiler->forceGcProfilerInfo = nullptr;
        }

        Release(profiler);
        return 0;
    }

    /// <summary>
    /// 在 GC 回调之外请求 CLR 分离，避免 RequestProfilerDetach 的超时占用 GC 回调线程。
    /// </summary>
    /// <remarks>
    /// 参数持有一个独立 AddRef 的 ProfilerInfo 引用；分离回调可以并发关闭 Profiler 自身的资源，
    /// 但不会释放这个工作线程持有的 COM 接口。请求失败时由 Controller 的 15 秒分离等待统一报告稳定错误。
    /// </remarks>
    DWORD WINAPI RequestProfilerDetachOnNativeThread(void* parameter)
    {
        auto* profilerInfo = static_cast<ICorProfilerInfo3*>(parameter);
        if (profilerInfo != nullptr)
        {
            // 给当前 GC 回调返回安全点留下一个很小的窗口；零会要求 CLR 使用其默认延迟，
            // 从而在连续附加场景中把每轮分离扩大到数秒。
            (void)profilerInfo->RequestProfilerDetach(100);
            profilerInfo->Release();
        }

        return 0;
    }

    /// <summary>
    /// 捕获一个对象及其出边；仅写入预分配记录区且不在 ObjectReferences 回调中检查 ObjectID。
    /// </summary>
    HRESULT STDMETHODCALLTYPE ObjectReferences(RawRetentionProfiler* profiler, ObjectID objectId, ClassID classId, ULONG referenceCount, ObjectID references[])
    {
        if (profiler->header == nullptr || profiler->overflowed
            || InterlockedCompareExchange(&profiler->header->status, 0, 0) != static_cast<LONG>(DotnetAnalysis::RetentionProfilerCaptureStatus::Capturing))
        {
            return S_OK;
        }
        const LONG objectSlot = ReserveSlot(profiler, &profiler->header->objectCount, profiler->header->objectCapacity);
        if (objectSlot < 0)
        {
            return S_OK;
        }
        auto* objects = reinterpret_cast<DotnetAnalysis::RetentionProfilerObjectRecord*>(reinterpret_cast<BYTE*>(profiler->header) + profiler->header->objectOffset);
        objects[objectSlot] =
        {
            static_cast<UINT_PTR>(objectId),
            static_cast<UINT_PTR>(classId),
            0
        };
        auto* edges = reinterpret_cast<DotnetAnalysis::RetentionProfilerEdgeRecord*>(reinterpret_cast<BYTE*>(profiler->header) + profiler->header->edgeOffset);
        for (ULONG index = 0; index < referenceCount && !profiler->overflowed; ++index)
        {
            const LONG edgeSlot = ReserveSlot(profiler, &profiler->header->edgeCount, profiler->header->edgeCapacity);
            if (edgeSlot >= 0)
            {
                edges[edgeSlot] = { static_cast<UINT_PTR>(objectId), static_cast<UINT_PTR>(references[index]) };
            }
        }
        return S_OK;
    }

    /// <summary>
    /// 捕获 RootReferences2 的真实根类别、标志与 rootId；栈根 rootId 原样保留为 FunctionID。
    /// </summary>
    HRESULT STDMETHODCALLTYPE RootReferences2(RawRetentionProfiler* profiler, ULONG rootCount, ObjectID roots[], COR_PRF_GC_ROOT_KIND kinds[], COR_PRF_GC_ROOT_FLAGS flags[], UINT_PTR rootIds[])
    {
        if (profiler->header == nullptr || profiler->overflowed
            || InterlockedCompareExchange(&profiler->header->status, 0, 0) != static_cast<LONG>(DotnetAnalysis::RetentionProfilerCaptureStatus::Capturing))
        {
            return S_OK;
        }
        auto* records = reinterpret_cast<DotnetAnalysis::RetentionProfilerRootRecord*>(reinterpret_cast<BYTE*>(profiler->header) + profiler->header->rootOffset);
        for (ULONG index = 0; index < rootCount && !profiler->overflowed; ++index)
        {
            if (roots[index] == 0)
            {
                continue;
            }
            const LONG slot = ReserveSlot(profiler, &profiler->header->rootCount, profiler->header->rootCapacity);
            if (slot >= 0)
            {
                records[slot] =
                {
                    static_cast<UINT_PTR>(roots[index]),
                    static_cast<std::uint32_t>(kinds[index]),
                    static_cast<std::uint32_t>(flags[index]),
                    rootIds[index],
                    DotnetAnalysis::kNoFunctionEvidence,
                    0
                };
            }
        }
        return S_OK;
    }

    /// <summary>
    /// 使用 CLR FunctionID 读取方法令牌、声明类型和模块名；失败时返回空字符串而不伪造函数证据。
    /// </summary>
    bool ResolveFunctionEvidence(
        ICorProfilerInfo3* profilerInfo,
        UINT_PTR functionId,
        DotnetAnalysis::RetentionProfilerFunctionEvidenceRecord* evidence)
    {
        ClassID classId{};
        ModuleID moduleId{};
        mdToken token{};
        if (profilerInfo == nullptr
            || FAILED(profilerInfo->GetFunctionInfo2(
                static_cast<FunctionID>(functionId),
                0,
                &classId,
                &moduleId,
                &token,
                0,
                nullptr,
                nullptr))
            || TypeFromToken(token) != mdtMethodDef)
        {
            return false;
        }

        WCHAR methodName[512]{};
        WCHAR typeName[512]{};
        WCHAR moduleName[260]{};
        mdTypeDef typeToken{};
        ULONG methodLength{};
        DWORD attributes{};
        PCCOR_SIGNATURE signature{};
        ULONG signatureLength{};
        ULONG rva{};
        DWORD implementationFlags{};
        IUnknown* metadataUnknown = nullptr;
        if (FAILED(profilerInfo->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport2, &metadataUnknown)) || metadataUnknown == nullptr)
        {
            return false;
        }
        auto* metadata = static_cast<IMetaDataImport2*>(metadataUnknown);
        const HRESULT methodResult = metadata->GetMethodProps(
            static_cast<mdMethodDef>(token),
            &typeToken,
            methodName,
            _countof(methodName),
            &methodLength,
            &attributes,
            &signature,
            &signatureLength,
            &rva,
            &implementationFlags);
        ULONG typeLength{};
        DWORD typeFlags{};
        mdToken extends{};
        const HRESULT typeResult = SUCCEEDED(methodResult)
            ? metadata->GetTypeDefProps(typeToken, typeName, _countof(typeName), &typeLength, &typeFlags, &extends)
            : methodResult;
        metadata->Release();
        ULONG moduleLength{};
        const HRESULT moduleResult = SUCCEEDED(typeResult)
            ? profilerInfo->GetModuleInfo(moduleId, nullptr, _countof(moduleName), &moduleLength, moduleName, nullptr)
            : typeResult;
        if (FAILED(moduleResult) || methodLength == 0 || typeLength == 0)
        {
            return false;
        }

        evidence->functionId = functionId;
        if (FAILED(StringCchPrintfW(evidence->functionName, _countof(evidence->functionName), L"%s.%s", typeName, methodName)))
        {
            evidence->functionName[0] = L'\0';
        }
        wcsncpy_s(evidence->moduleName, moduleName, _TRUNCATE);
        return evidence->functionName[0] != L'\0';
    }

    /// <summary>
    /// 将 CLR 元素类型转换为稳定的托管类型名；仅处理没有基类 ClassID 的数组元素类型。
    /// </summary>
    const wchar_t* GetPrimitiveTypeName(CorElementType elementType)
    {
        switch (elementType)
        {
        case ELEMENT_TYPE_BOOLEAN: return L"System.Boolean";
        case ELEMENT_TYPE_CHAR: return L"System.Char";
        case ELEMENT_TYPE_I1: return L"System.SByte";
        case ELEMENT_TYPE_U1: return L"System.Byte";
        case ELEMENT_TYPE_I2: return L"System.Int16";
        case ELEMENT_TYPE_U2: return L"System.UInt16";
        case ELEMENT_TYPE_I4: return L"System.Int32";
        case ELEMENT_TYPE_U4: return L"System.UInt32";
        case ELEMENT_TYPE_I8: return L"System.Int64";
        case ELEMENT_TYPE_U8: return L"System.UInt64";
        case ELEMENT_TYPE_R4: return L"System.Single";
        case ELEMENT_TYPE_R8: return L"System.Double";
        case ELEMENT_TYPE_I: return L"System.IntPtr";
        case ELEMENT_TYPE_U: return L"System.UIntPtr";
        case ELEMENT_TYPE_STRING: return L"System.String";
        case ELEMENT_TYPE_OBJECT: return L"System.Object";
        default: return nullptr;
        }
    }

    /// <summary>
    /// 从元数据令牌构造包含外层声明类型的全名；递归深度固定，避免恶意元数据造成无界栈增长。
    /// </summary>
    bool ResolveTypeDefinitionName(
        IMetaDataImport2* metadata,
        mdTypeDef typeToken,
        wchar_t* destination,
        std::size_t destinationLength,
        std::size_t depth)
    {
        if (metadata == nullptr || destination == nullptr || destinationLength == 0 || depth >= kMaximumTypeResolutionDepth)
        {
            return false;
        }

        WCHAR declaredName[512]{};
        ULONG declaredNameLength{};
        DWORD typeFlags{};
        mdToken extends{};
        if (FAILED(metadata->GetTypeDefProps(
                typeToken,
                declaredName,
                _countof(declaredName),
                &declaredNameLength,
                &typeFlags,
                &extends))
            || declaredNameLength == 0)
        {
            return false;
        }

        mdTypeDef enclosingType{};
        if (FAILED(metadata->GetNestedClassProps(typeToken, &enclosingType)))
        {
            return SUCCEEDED(StringCchCopyW(destination, destinationLength, declaredName));
        }

        WCHAR enclosingName[512]{};
        return ResolveTypeDefinitionName(metadata, enclosingType, enclosingName, _countof(enclosingName), depth + 1)
            && SUCCEEDED(StringCchPrintfW(destination, destinationLength, L"%s+%s", enclosingName, declaredName));
    }

    /// <summary>
    /// 使用 CLR ClassID 与 Metadata API 解析真实类型名和模块；固定缓冲区和深度保证完成回调内资源有界。
    /// </summary>
    bool ResolveTypeEvidence(
        ICorProfilerInfo3* profilerInfo,
        ClassID classId,
        DotnetAnalysis::RetentionProfilerTypeEvidenceRecord* evidence,
        std::size_t depth = 0)
    {
        if (profilerInfo == nullptr || classId == 0 || evidence == nullptr || depth >= kMaximumTypeResolutionDepth)
        {
            return false;
        }

        ModuleID moduleId{};
        mdTypeDef typeToken{};
        ClassID parentClassId{};
        ULONG32 typeArgumentCount{};
        const HRESULT classResult = profilerInfo->GetClassIDInfo2(
            classId,
            &moduleId,
            &typeToken,
            &parentClassId,
            0,
            &typeArgumentCount,
            nullptr);
        if (classResult == CORPROF_E_CLASSID_IS_ARRAY)
        {
            CorElementType elementType{};
            ClassID elementClassId{};
            ULONG rank{};
            if (FAILED(profilerInfo->IsArrayClass(classId, &elementType, &elementClassId, &rank)) || rank == 0 || rank > 64)
            {
                return false;
            }

            WCHAR elementName[512]{};
            WCHAR elementModuleName[260]{};
            if (elementClassId != 0)
            {
                DotnetAnalysis::RetentionProfilerTypeEvidenceRecord elementEvidence{};
                if (!ResolveTypeEvidence(profilerInfo, elementClassId, &elementEvidence, depth + 1))
                {
                    return false;
                }
                if (FAILED(StringCchCopyW(elementName, _countof(elementName), elementEvidence.typeName)))
                {
                    return false;
                }
                (void)StringCchCopyW(elementModuleName, _countof(elementModuleName), elementEvidence.moduleName);
            }
            else
            {
                const wchar_t* primitiveName = GetPrimitiveTypeName(elementType);
                if (primitiveName == nullptr || FAILED(StringCchCopyW(elementName, _countof(elementName), primitiveName)))
                {
                    return false;
                }
            }

            if (FAILED(StringCchCopyW(evidence->typeName, _countof(evidence->typeName), elementName))
                || FAILED(StringCchCatW(evidence->typeName, _countof(evidence->typeName), L"[")))
            {
                return false;
            }
            for (ULONG dimension = 1; dimension < rank; ++dimension)
            {
                if (FAILED(StringCchCatW(evidence->typeName, _countof(evidence->typeName), L",")))
                {
                    return false;
                }
            }
            if (FAILED(StringCchCatW(evidence->typeName, _countof(evidence->typeName), L"]")))
            {
                return false;
            }
            evidence->classId = static_cast<UINT_PTR>(classId);
            (void)StringCchCopyW(evidence->moduleName, _countof(evidence->moduleName), elementModuleName);
            return true;
        }
        if (FAILED(classResult) || moduleId == 0 || TypeFromToken(typeToken) != mdtTypeDef)
        {
            return false;
        }

        IUnknown* metadataUnknown = nullptr;
        if (FAILED(profilerInfo->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport2, &metadataUnknown)) || metadataUnknown == nullptr)
        {
            return false;
        }
        auto* metadata = static_cast<IMetaDataImport2*>(metadataUnknown);
        const bool resolved = ResolveTypeDefinitionName(
            metadata,
            typeToken,
            evidence->typeName,
            _countof(evidence->typeName),
            0);
        metadata->Release();
        if (!resolved)
        {
            return false;
        }

        evidence->classId = static_cast<UINT_PTR>(classId);
        ULONG moduleNameLength{};
        if (FAILED(profilerInfo->GetModuleInfo(
            moduleId,
            nullptr,
            _countof(evidence->moduleName),
            &moduleNameLength,
            evidence->moduleName,
            nullptr)))
        {
            evidence->moduleName[0] = L'\0';
        }
        return true;
    }

    /// <summary>
    /// 为一个 ClassID 注册至多一条类型证据；实例内固定哈希表避免按对象数线性扫描类型表。
    /// </summary>
    void RegisterTypeEvidence(RawRetentionProfiler* profiler, ClassID classId)
    {
        if (classId == 0 || profiler->header->typeCapacity == 0)
        {
            return;
        }

        auto hash = static_cast<std::size_t>(classId);
        hash ^= hash >> 33;
        hash *= static_cast<std::size_t>(0xff51afd7ed558ccdULL);
        hash ^= hash >> 33;
        std::size_t slot = hash & (kTypeHashCapacity - 1);
        for (std::size_t probe = 0; probe < kTypeHashCapacity; ++probe)
        {
            if (profiler->typeHashKeys[slot] == static_cast<UINT_PTR>(classId))
            {
                return;
            }
            if (profiler->typeHashKeys[slot] == 0)
            {
                profiler->typeHashKeys[slot] = static_cast<UINT_PTR>(classId);
                profiler->typeHashEvidenceIndexes[slot] = DotnetAnalysis::kNoFunctionEvidence;
                const LONG existingCount = profiler->header->typeCount;
                if (static_cast<std::uint32_t>(existingCount) >= profiler->header->typeCapacity)
                {
                    return;
                }

                DotnetAnalysis::RetentionProfilerTypeEvidenceRecord candidate{};
                if (!ResolveTypeEvidence(profiler->profilerInfo, classId, &candidate))
                {
                    return;
                }
                const LONG reserved = ReserveSlot(profiler, &profiler->header->typeCount, profiler->header->typeCapacity);
                if (reserved >= 0)
                {
                    auto* types = reinterpret_cast<DotnetAnalysis::RetentionProfilerTypeEvidenceRecord*>(
                        reinterpret_cast<BYTE*>(profiler->header) + profiler->header->typeOffset);
                    types[reserved] = candidate;
                    profiler->typeHashEvidenceIndexes[slot] = static_cast<std::uint32_t>(reserved);
                }
                return;
            }
            slot = (slot + 1) & (kTypeHashCapacity - 1);
        }
    }

    /// <summary>
    /// 图记录冻结后扫描对象 ClassID，并为固定容量内的不同类型生成元数据证据。
    /// </summary>
    void ResolveHeapTypes(RawRetentionProfiler* profiler)
    {
        if (profiler->header == nullptr || profiler->profilerInfo == nullptr)
        {
            return;
        }
        auto* objects = reinterpret_cast<DotnetAnalysis::RetentionProfilerObjectRecord*>(
            reinterpret_cast<BYTE*>(profiler->header) + profiler->header->objectOffset);
        const LONG objectCount = profiler->header->objectCount;
        for (LONG objectIndex = 0; objectIndex < objectCount; ++objectIndex)
        {
            RegisterTypeEvidence(profiler, static_cast<ClassID>(objects[objectIndex].classId));
            if ((objectIndex & 0xffff) == 0)
            {
                InterlockedExchange64(&profiler->header->lastProgressTickCount, GetTickCount64());
            }
        }
    }

    /// <summary>
    /// 在 GarbageCollectionFinished 的 CLR 合法检查时点读取对象大小。
    /// </summary>
    /// <remarks>
    /// ObjectReferences 回调中的 ObjectID 不可检查；GC 完成后才访问它们，且整个解析发生在下一次 GC
    /// 开始前。GetObjectSize 失败时保留零表示未知，不把失败伪装成尺寸值。
    /// </remarks>
    void ResolveHeapObjectSizes(RawRetentionProfiler* profiler)
    {
        if (profiler->header == nullptr || profiler->profilerInfo == nullptr)
        {
            return;
        }

        auto* objects = reinterpret_cast<DotnetAnalysis::RetentionProfilerObjectRecord*>(
            reinterpret_cast<BYTE*>(profiler->header) + profiler->header->objectOffset);
        const LONG objectCount = profiler->header->objectCount;
        for (LONG objectIndex = 0; objectIndex < objectCount; ++objectIndex)
        {
            ULONG objectSize{};
            if (SUCCEEDED(profiler->profilerInfo->GetObjectSize(
                    static_cast<ObjectID>(objects[objectIndex].objectId),
                    &objectSize)))
            {
                objects[objectIndex].sizeBytes = static_cast<UINT_PTR>(objectSize);
            }

            if ((objectIndex & 0xffff) == 0)
            {
                InterlockedExchange64(&profiler->header->lastProgressTickCount, GetTickCount64());
            }
        }
    }

    /// <summary>
    /// 冻结前为最多预分配容量内的不同栈根 FunctionID 生成函数证据，并回填根记录索引。
    /// </summary>
    void ResolveStackRootFunctions(RawRetentionProfiler* profiler)
    {
        if (profiler->header == nullptr || profiler->profilerInfo == nullptr)
        {
            return;
        }
        auto* roots = reinterpret_cast<DotnetAnalysis::RetentionProfilerRootRecord*>(reinterpret_cast<BYTE*>(profiler->header) + profiler->header->rootOffset);
        auto* functions = reinterpret_cast<DotnetAnalysis::RetentionProfilerFunctionEvidenceRecord*>(reinterpret_cast<BYTE*>(profiler->header) + profiler->header->functionOffset);
        const LONG rootCount = profiler->header->rootCount;
        for (LONG rootIndex = 0; rootIndex < rootCount; ++rootIndex)
        {
            auto& root = roots[rootIndex];
            if (root.rootKind != COR_PRF_GC_ROOT_STACK || root.rootId == 0)
            {
                continue;
            }
            LONG functionIndex = -1;
            const LONG existingCount = profiler->header->functionCount;
            for (LONG index = 0; index < existingCount; ++index)
            {
                if (functions[index].functionId == root.rootId)
                {
                    functionIndex = index;
                    break;
                }
            }
            if (functionIndex < 0 && static_cast<std::uint32_t>(existingCount) < profiler->header->functionCapacity)
            {
                const LONG reserved = ReserveSlot(profiler, &profiler->header->functionCount, profiler->header->functionCapacity);
                if (reserved >= 0 && ResolveFunctionEvidence(profiler->profilerInfo, root.rootId, &functions[reserved]))
                {
                    functionIndex = reserved;
                }
                else if (reserved >= 0)
                {
                    functions[reserved].functionId = 0;
                }
            }
            if (functionIndex >= 0)
            {
                root.functionEvidenceIndex = static_cast<std::uint32_t>(functionIndex);
            }
        }
    }

    /// <summary>
    /// GC 开始时标记采集中状态；CLR 可能在多个线程回调，所有计数使用原子操作。
    /// </summary>
    HRESULT STDMETHODCALLTYPE GarbageCollectionStarted(RawRetentionProfiler* profiler, int, BOOL[], COR_PRF_GC_REASON)
    {
        if (profiler->header != nullptr)
        {
            const LONG previous = InterlockedCompareExchange(
                &profiler->header->status,
                static_cast<LONG>(DotnetAnalysis::RetentionProfilerCaptureStatus::Capturing),
                static_cast<LONG>(DotnetAnalysis::RetentionProfilerCaptureStatus::Pending));
            if (previous == static_cast<LONG>(DotnetAnalysis::RetentionProfilerCaptureStatus::Pending))
            {
                InterlockedExchange64(&profiler->header->lastProgressTickCount, GetTickCount64());
            }
        }
        return S_OK;
    }

    /// <summary>
    /// GC 完成后冻结记录并通知 Controller；只在成功冻结后请求 CLR 分离 Profiler。
    /// </summary>
    HRESULT STDMETHODCALLTYPE GarbageCollectionFinished(RawRetentionProfiler* profiler)
    {
        if (profiler->header == nullptr || profiler->overflowed
            || InterlockedCompareExchange(&profiler->header->status, 0, 0) != static_cast<LONG>(DotnetAnalysis::RetentionProfilerCaptureStatus::Capturing))
        {
            return S_OK;
        }
        ResolveHeapObjectSizes(profiler);
        ResolveHeapTypes(profiler);
        ResolveStackRootFunctions(profiler);
        if (profiler->overflowed)
        {
            return S_OK;
        }
        MemoryBarrier();
        InterlockedExchange(&profiler->header->status, static_cast<LONG>(DotnetAnalysis::RetentionProfilerCaptureStatus::Completed));
        SetEvent(profiler->completionEvent);
        if (profiler->profilerInfo != nullptr)
        {
            profiler->profilerInfo->AddRef();
            const HANDLE detachThread = CreateThread(
                nullptr,
                0,
                RequestProfilerDetachOnNativeThread,
                profiler->profilerInfo,
                0,
                nullptr);
            if (detachThread == nullptr)
            {
                profiler->profilerInfo->Release();
            }
            else
            {
                CloseHandle(detachThread);
            }
        }
        return S_OK;
    }

    /// <summary>
    /// 初始化附加会话：打开 Controller 创建的共享对象、验证布局、启用 GC 回调并保留 ProfilerInfo3。
    /// </summary>
    HRESULT STDMETHODCALLTYPE InitializeForAttach(RawRetentionProfiler* profiler, IUnknown* unknown, void* clientData, UINT clientDataLength)
    {
        if (unknown == nullptr || clientData == nullptr || clientDataLength != sizeof(DotnetAnalysis::RetentionProfilerAttachData))
        {
            return E_INVALIDARG;
        }
        const auto& data = *static_cast<const DotnetAnalysis::RetentionProfilerAttachData*>(clientData);
        if (!DotnetAnalysis::IsValidRetentionProfilerAttachData(data))
        {
            return E_INVALIDARG;
        }
        if (FAILED(unknown->QueryInterface(__uuidof(ICorProfilerInfo3), reinterpret_cast<void**>(&profiler->profilerInfo))))
        {
            return E_NOINTERFACE;
        }
        profiler->mappingHandle = OpenFileMappingW(FILE_MAP_WRITE, FALSE, data.mappingName);
        profiler->completionEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, data.completionEventName);
        profiler->failureEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, data.failureEventName);
        profiler->detachEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, data.detachEventName);
        if (profiler->mappingHandle == nullptr || profiler->completionEvent == nullptr || profiler->failureEvent == nullptr || profiler->detachEvent == nullptr)
        {
            CloseResources(profiler);
            return HRESULT_FROM_WIN32(GetLastError());
        }
        profiler->header = static_cast<DotnetAnalysis::RetentionProfilerSharedHeader*>(MapViewOfFile(profiler->mappingHandle, FILE_MAP_WRITE, 0, 0, data.mappingCapacityBytes));
        if (profiler->header == nullptr
            || profiler->header->magic != DotnetAnalysis::kRetentionProfilerSharedMemoryMagic
            || profiler->header->version != DotnetAnalysis::kRetentionProfilerProtocolVersion
            || profiler->header->capacityBytes != data.mappingCapacityBytes
            || !IsRegionValid(profiler->header->objectOffset, profiler->header->objectCapacity, sizeof(DotnetAnalysis::RetentionProfilerObjectRecord), data.mappingCapacityBytes)
            || !IsRegionValid(profiler->header->edgeOffset, profiler->header->edgeCapacity, sizeof(DotnetAnalysis::RetentionProfilerEdgeRecord), data.mappingCapacityBytes)
            || !IsRegionValid(profiler->header->rootOffset, profiler->header->rootCapacity, sizeof(DotnetAnalysis::RetentionProfilerRootRecord), data.mappingCapacityBytes)
            || !IsRegionValid(profiler->header->typeOffset, profiler->header->typeCapacity, sizeof(DotnetAnalysis::RetentionProfilerTypeEvidenceRecord), data.mappingCapacityBytes))
        {
            CloseResources(profiler);
            return E_INVALIDARG;
        }
        if (!IsRegionValid(profiler->header->functionOffset, profiler->header->functionCapacity, sizeof(DotnetAnalysis::RetentionProfilerFunctionEvidenceRecord), data.mappingCapacityBytes))
        {
            CloseResources(profiler);
            return E_INVALIDARG;
        }
        return profiler->profilerInfo->SetEventMask(COR_PRF_MONITOR_GC);
    }

    /// <summary>
    /// CLR 宣告附加成功后转交原生线程触发一次 GC；回调线程本身不允许调用 ForceGC。
    /// </summary>
    HRESULT STDMETHODCALLTYPE ProfilerAttachComplete(RawRetentionProfiler* profiler)
    {
        if (profiler->profilerInfo == nullptr)
        {
            FailCapture(profiler, E_POINTER);
            return S_OK;
        }

        profiler->profilerInfo->AddRef();
        profiler->forceGcProfilerInfo = profiler->profilerInfo;
        AddRef(profiler);
        const HANDLE forceGcThread = CreateThread(nullptr, 0, ForceGarbageCollectionOnNativeThread, profiler, 0, nullptr);
        if (forceGcThread == nullptr)
        {
            profiler->forceGcProfilerInfo->Release();
            profiler->forceGcProfilerInfo = nullptr;
            Release(profiler);
            FailCapture(profiler, HRESULT_FROM_WIN32(GetLastError()));
            return S_OK;
        }
        CloseHandle(forceGcThread);
        return S_OK;
    }

    /// <summary>
    /// CLR 确认分离后释放映射和事件句柄；Controller 已在完成事件后读取冻结数据。
    /// </summary>
    HRESULT STDMETHODCALLTYPE ProfilerDetachSucceeded(RawRetentionProfiler* profiler)
    {
        if (profiler->detachEvent != nullptr)
        {
            SetEvent(profiler->detachEvent);
        }
        CloseResources(profiler);
        return S_OK;
    }

    /// <summary>
    /// 初始化 Callback3 原始 vtable，将未使用槽位安全地绑定到无副作用回调。
    /// </summary>
    void** GetProfilerVtable()
    {
        static void* table[kCallback3VtableLength]{};
        static const bool initialized = []
        {
            for (auto& entry : table)
            {
                entry = GetVtableSlot(&NoopCallback);
            }
            table[0] = GetVtableSlot(&QueryInterface);
            table[1] = GetVtableSlot(&AddRef);
            table[2] = GetVtableSlot(&Release);
            table[kInitializeSlot] = GetVtableSlot(&Initialize);
            table[kShutdownSlot] = GetVtableSlot(&Shutdown);
            table[kObjectReferencesSlot] = GetVtableSlot(&ObjectReferences);
            table[kGarbageCollectionStartedSlot] = GetVtableSlot(&GarbageCollectionStarted);
            table[kGarbageCollectionFinishedSlot] = GetVtableSlot(&GarbageCollectionFinished);
            table[kRootReferences2Slot] = GetVtableSlot(&RootReferences2);
            table[kInitializeForAttachSlot] = GetVtableSlot(&InitializeForAttach);
            table[kProfilerAttachCompleteSlot] = GetVtableSlot(&ProfilerAttachComplete);
            table[kProfilerDetachSucceededSlot] = GetVtableSlot(&ProfilerDetachSucceeded);
            return true;
        }();
        (void)initialized;
        return table;
    }

    /// <summary>
    /// 创建只支持非聚合实例的 Profiler COM 对象。
    /// </summary>
    HRESULT CreateProfilerInstance(REFIID riid, void** result)
    {
        if (result == nullptr)
        {
            return E_POINTER;
        }
        *result = nullptr;
        auto* profiler = new (std::nothrow) RawRetentionProfiler{ GetProfilerVtable(), 1, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, false };
        if (profiler == nullptr)
        {
            return E_OUTOFMEMORY;
        }
        ++s_activeProfilerObjects;
        const HRESULT queryResult = QueryInterface(profiler, riid, result);
        Release(profiler);
        return queryResult;
    }

    /// <summary>
    /// 提供 CLR 所需的 IClassFactory 实现并在它创建 Profiler 实例时保持 COM 所有权边界。
    /// </summary>
    class RetentionProfilerClassFactory final : public IClassFactory
    {
    public:
        RetentionProfilerClassFactory() { ++s_activeClassFactories; }
        ~RetentionProfilerClassFactory() { --s_activeClassFactories; }
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** result) override
        {
            if (result == nullptr) return E_POINTER;
            *result = nullptr;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *result = this;
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++_references; }
        ULONG STDMETHODCALLTYPE Release() override { const ULONG count = --_references; if (count == 0) delete this; return count; }
        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** result) override
        {
            return outer == nullptr ? CreateProfilerInstance(riid, result) : CLASS_E_NOAGGREGATION;
        }
        HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
        {
            if (lock)
            {
                ++s_serverLocks;
            }
            else
            {
                --s_serverLocks;
            }
            return S_OK;
        }
    private:
        std::atomic<ULONG> _references{ 1 };
    };
}

/// <summary>
/// 导出 CLR 发现 Profiler COM 类工厂所需的入口。
/// </summary>
STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** result)
{
    if (!InlineIsEqualGUID(clsid, DotnetAnalysis::kRetentionProfilerClsid))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    auto* factory = new (std::nothrow) RetentionProfilerClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }
    const HRESULT queryResult = factory->QueryInterface(riid, result);
    factory->Release();
    return queryResult;
}

/// <summary>
/// 只有不存在活动 Profiler、类工厂和服务器锁时，CLR 才可卸载 DLL，避免回调或工作线程执行期间卸载代码页。
/// </summary>
STDAPI DllCanUnloadNow()
{
    return s_activeProfilerObjects.load() == 0
        && s_activeClassFactories.load() == 0
        && s_serverLocks.load() == 0
        ? S_OK
        : S_FALSE;
}
