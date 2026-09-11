#pragma once

#include <windows.h>

#include <cstddef>
#include <cstdint>
#include <limits>

namespace DotnetAnalysis
{
    /// <summary>
    /// 共享内存协议的固定版本；Controller 与 Profiler 必须完全一致才允许附加。
    /// </summary>
    constexpr std::uint32_t kRetentionProfilerProtocolVersion = 6;

    /// <summary>
    /// v6 附加协议可携带的预分配共享段上限；Profiler 不会在 CLR 回调内创建额外命名对象。
    /// </summary>
    constexpr std::uint32_t kRetentionProfilerMaximumSegmentCount = 4;

    /// <summary>
    /// 共享内存头部固定魔数，用于防止错误命名对象或陈旧映射被当作有效捕获结果。
    /// </summary>
    constexpr std::uint32_t kRetentionProfilerSharedMemoryMagic = 0x50415244;

    /// <summary>
    /// 保留分析 Profiler 的唯一 COM 类标识；Controller 和受管调用方必须使用此值附加。
    /// </summary>
    inline constexpr CLSID kRetentionProfilerClsid =
    { 0x1D6F4A88, 0x0F8B, 0x4B93, { 0xA1, 0x2C, 0x1D, 0xCA, 0xB9, 0x69, 0x6F, 0x13 } };

    /// <summary>
    /// 通过 ICLRProfiling::AttachProfiler 的客户端数据传递给 InitializeForAttach 的固定布局。
    /// </summary>
    /// <remarks>
    /// 名称由 Controller 进程创建并在 Profiler 退出前保持有效。Profiler 回调只使用已打开的映射和事件句柄，
    /// 不进行 IPC 等待、磁盘写入或无界动态分配。
    /// </remarks>
    struct RetentionProfilerAttachData final
    {
        /// <summary>协议版本。</summary>
        std::uint32_t version;

        /// <summary>每个共享段容量，必须包含 <see cref="RetentionProfilerSharedHeader"/>。</summary>
        std::uint32_t segmentCapacityBytes;

        /// <summary>Controller 预分配的共享段数量，范围为 2 至 <see cref="kRetentionProfilerMaximumSegmentCount"/>。</summary>
        std::uint32_t segmentCount;

        /// <summary>Controller 预分配的共享段命名映射；未使用项以空字符串表示。</summary>
        wchar_t segmentNames[kRetentionProfilerMaximumSegmentCount][260];

        /// <summary>Profiler 完成一次 GC 数据采集后设置的命名事件。</summary>
        wchar_t completionEventName[260];

        /// <summary>Profiler 在协议或容量失败时设置的命名事件。</summary>
        wchar_t failureEventName[260];

        /// <summary>CLR 确认 Profiler 已完成分离时设置的命名事件。</summary>
        wchar_t detachEventName[260];
    };

    /// <summary>
    /// 共享内存内捕获状态；状态只从 Pending 推进到 Capturing、Finalizing、Completed 或 Failed。
    /// </summary>
    enum class RetentionProfilerCaptureStatus : LONG
    {
        /// <summary>Controller 已创建映射，Profiler 尚未开始写入。</summary>
        Pending = 0,

        /// <summary>GC 回调正在向预分配区域写入原始记录。</summary>
        Capturing = 1,

        /// <summary>图回调已结束，Controller 可以并发转存对象身份和引用边；对象大小及根函数证据仍在 CLR 合法时点回填。</summary>
        Finalizing = 2,

        /// <summary>所有记录和证据已冻结，可由 Controller 完成最终补丁和落盘。</summary>
        Completed = 3,

        /// <summary>协议无效、记录溢出或 CLR 回调失败，结果不得读取。</summary>
        Failed = 4
    };

    /// <summary>
    /// 单个共享段的生产者/消费者生命周期；发布与确认均通过原子状态转换建立跨进程可见性边界。
    /// </summary>
    enum class RetentionProfilerSegmentState : LONG
    {
        /// <summary>Controller 已完整转存上一代，生产者可以立即获取。</summary>
        Reusable = 0,

        /// <summary>单个生产者正在重置可复用 payload，其他写入者不得进入。</summary>
        Initializing = 1,

        /// <summary>Profiler 回调可以预留并写入固定宽度记录。</summary>
        Writing = 2,

        /// <summary>发布者正在分配全局单调序列，写入者只允许退出。</summary>
        AssigningSequence = 3,

        /// <summary>已禁止新写入，等待当前段内最后一个写入者退出。</summary>
        Sealing = 4,

        /// <summary>记录和计数已经冻结，Controller 可以读取并确认。</summary>
        Published = 5
    };

    /// <summary>
    /// Profiler 要写入共享段的图记录类型；获取段时据此在私有 Initializing 状态内验证对应容量。
    /// </summary>
    enum class RetentionProfilerGraphRecordKind : std::uint32_t
    {
        /// <summary>对象身份 ledger 记录，跨发布代保留。</summary>
        Object = 0,

        /// <summary>对象引用边 payload，确认复用后清零。</summary>
        Edge = 1,

        /// <summary>GC 根 ledger 记录，跨发布代保留。</summary>
        Root = 2
    };

    /// <summary>
    /// 共享映射固定头部；对象和根计数跨代单调递增，边计数由生产者在每次确认复用后清零。
    /// </summary>
    struct RetentionProfilerSharedHeader final
    {
        /// <summary>固定魔数。</summary>
        std::uint32_t magic;

        /// <summary>协议版本。</summary>
        std::uint32_t version;

        /// <summary>整个映射容量。</summary>
        std::uint32_t capacityBytes;

        /// <summary>当前状态，由 InterlockedExchange 更新。</summary>
        volatile LONG status;

        /// <summary>对象记录区的偏移和容量。</summary>
        std::uint32_t objectOffset;
        std::uint32_t objectCapacity;
        volatile LONG objectCount;

        /// <summary>引用边记录区的偏移和容量。</summary>
        std::uint32_t edgeOffset;
        std::uint32_t edgeCapacity;
        volatile LONG edgeCount;

        /// <summary>GC 根记录区的偏移和容量。</summary>
        std::uint32_t rootOffset;
        std::uint32_t rootCapacity;
        volatile LONG rootCount;

        /// <summary>函数证据记录区的偏移和容量；只保存经 CLR FunctionID 解析的栈根。</summary>
        std::uint32_t functionOffset;
        std::uint32_t functionCapacity;
        volatile LONG functionCount;

        /// <summary>类型证据记录区的偏移和容量；只保存经 CLR Metadata API 解析的 ClassID。</summary>
        std::uint32_t typeOffset;
        std::uint32_t typeCapacity;
        volatile LONG typeCount;

        /// <summary>失败时写入导致捕获终止的首个 HRESULT；成功和待采集状态为 S_OK。</summary>
        volatile LONG failureHResult;

        /// <summary>捕获期间最后一次进度单调时间戳，Controller 用于 60 秒无进度看门狗。</summary>
        volatile LONGLONG lastProgressTickCount;

        /// <summary>当前段生命周期；Published 的 release 写与 Controller 的 acquire 读隔离半写记录。</summary>
        volatile LONG segmentState;

        /// <summary>低 31 位是已登记且尚未退出的 writer 数；符号位在 publisher 关闭新登记后置位。</summary>
        volatile LONG activeWriterCount;

        /// <summary>当前 Published 代的全局单调序列；同一段复用时必须严格增长。</summary>
        volatile LONGLONG publicationSequence;

        /// <summary>Controller 最后完整落盘并确认的发布序列；生产者只复用相等代。</summary>
        volatile LONGLONG acknowledgedSequence;
    };

    /// <summary>
    /// 尝试立即获取一个已确认且能容纳指定记录的可复用段；获取成功时只清空可复用边 payload，保留对象与根 ledger 供最终合法回填。
    /// </summary>
    /// <param name="segments">Controller 预分配并已验证的共享段。</param>
    /// <param name="segmentCount">共享段数量。</param>
    /// <param name="nextSegmentIndex">输入首选索引，成功后推进到下一索引。</param>
    /// <param name="recordKind">当前调用需要预留的图记录类型。</param>
    /// <param name="protocolCorrupted">记录类型无效、确认序列不一致或私有初始化状态被破坏时设置为真。</param>
    /// <returns>立即取得的 Writing 段；没有可复用段或协议损坏时返回空。</returns>
    inline RetentionProfilerSharedHeader* TryAcquireRetentionProfilerSegment(
        RetentionProfilerSharedHeader* const segments[],
        std::uint32_t segmentCount,
        std::uint32_t* nextSegmentIndex,
        RetentionProfilerGraphRecordKind recordKind,
        bool* protocolCorrupted = nullptr) noexcept
    {
        if (segments == nullptr || segmentCount == 0 || nextSegmentIndex == nullptr)
        {
            return nullptr;
        }
        if (protocolCorrupted != nullptr)
        {
            *protocolCorrupted = false;
        }
        if (recordKind != RetentionProfilerGraphRecordKind::Object
            && recordKind != RetentionProfilerGraphRecordKind::Edge
            && recordKind != RetentionProfilerGraphRecordKind::Root)
        {
            if (protocolCorrupted != nullptr)
            {
                *protocolCorrupted = true;
            }
            return nullptr;
        }

        const std::uint32_t start = *nextSegmentIndex % segmentCount;
        for (std::uint32_t offset = 0; offset < segmentCount; ++offset)
        {
            const std::uint32_t index = (start + offset) % segmentCount;
            auto* header = segments[index];
            if (header == nullptr
                || InterlockedCompareExchange(
                    &header->segmentState,
                    static_cast<LONG>(RetentionProfilerSegmentState::Initializing),
                    static_cast<LONG>(RetentionProfilerSegmentState::Reusable))
                    != static_cast<LONG>(RetentionProfilerSegmentState::Reusable))
            {
                continue;
            }

            const LONGLONG publicationSequence = InterlockedCompareExchange64(&header->publicationSequence, 0, 0);
            const LONGLONG acknowledgedSequence = InterlockedCompareExchange64(&header->acknowledgedSequence, 0, 0);
            if (publicationSequence != acknowledgedSequence)
            {
                if (protocolCorrupted != nullptr)
                {
                    *protocolCorrupted = true;
                }
                return nullptr;
            }

            const LONG writerRegistrationState = InterlockedCompareExchange(&header->activeWriterCount, 0, 0);
            constexpr LONG writerRegistrationClosed = (std::numeric_limits<LONG>::min)();
            const LONG expectedWriterRegistrationState = publicationSequence == 0
                ? 0
                : writerRegistrationClosed;
            if (writerRegistrationState != expectedWriterRegistrationState)
            {
                if (protocolCorrupted != nullptr)
                {
                    *protocolCorrupted = true;
                }
                return nullptr;
            }

            const LONG persistentCount = recordKind == RetentionProfilerGraphRecordKind::Object
                ? InterlockedCompareExchange(&header->objectCount, 0, 0)
                : recordKind == RetentionProfilerGraphRecordKind::Root
                    ? InterlockedCompareExchange(&header->rootCount, 0, 0)
                    : 0;
            const std::uint32_t persistentCapacity = recordKind == RetentionProfilerGraphRecordKind::Object
                ? header->objectCapacity
                : header->rootCapacity;
            const bool hasCapacity = recordKind == RetentionProfilerGraphRecordKind::Edge
                ? header->edgeCapacity != 0
                : persistentCount >= 0 && static_cast<std::uint32_t>(persistentCount) < persistentCapacity;
            if (!hasCapacity)
            {
                if (InterlockedCompareExchange(
                        &header->segmentState,
                        static_cast<LONG>(RetentionProfilerSegmentState::Reusable),
                        static_cast<LONG>(RetentionProfilerSegmentState::Initializing))
                    != static_cast<LONG>(RetentionProfilerSegmentState::Initializing))
                {
                    if (protocolCorrupted != nullptr)
                    {
                        *protocolCorrupted = true;
                    }
                    return nullptr;
                }
                continue;
            }

            InterlockedExchange(&header->edgeCount, 0);
            InterlockedExchange(&header->activeWriterCount, 0);
            MemoryBarrier();
            InterlockedExchange(&header->segmentState, static_cast<LONG>(RetentionProfilerSegmentState::Writing));
            *nextSegmentIndex = (index + 1) % segmentCount;
            return header;
        }

        return nullptr;
    }

    /// <summary>
    /// 封装段内 writer 登记的原子步骤，供写入租约入口和确定性状态机测试复用。
    /// </summary>
    namespace RetentionProfilerProtocolDetail
    {
        /// <summary>关闭新 writer 登记的符号位；低 31 位继续保存尚未退出的登记数。</summary>
        constexpr LONG kWriterRegistrationClosed = (std::numeric_limits<LONG>::min)();

        /// <summary>从带注册门标志的原子字段中提取尚未退出的 writer 数。</summary>
        constexpr LONG kWriterCountMask = (std::numeric_limits<LONG>::max)();

        /// <summary>
        /// 在调用方已经观察到 Writing 后尝试登记一个待确认 writer；publisher 关闭注册门后立即拒绝。
        /// </summary>
        /// <param name="header">要登记 writer 的共享段。</param>
        /// <returns>登记成功时返回真。</returns>
        inline bool TryRegisterRetentionProfilerSegmentWriter(RetentionProfilerSharedHeader* header) noexcept
        {
            LONG current = InterlockedCompareExchange(&header->activeWriterCount, 0, 0);
            while ((current & kWriterRegistrationClosed) == 0 && current < kWriterCountMask)
            {
                const LONG observed = InterlockedCompareExchange(
                    &header->activeWriterCount,
                    current + 1,
                    current);
                if (observed == current)
                {
                    return true;
                }
                current = observed;
            }
            return false;
        }

        /// <summary>
        /// 释放一个已登记 writer；关闭注册后由最后一个退出者完成 Published release 转换。
        /// </summary>
        /// <param name="header">持有登记的共享段。</param>
        inline void ReleaseRetentionProfilerSegmentWriter(RetentionProfilerSharedHeader* header) noexcept
        {
            const LONG remaining = InterlockedDecrement(&header->activeWriterCount);
            if ((remaining & kWriterCountMask) == 0
                && InterlockedCompareExchange(
                    &header->segmentState,
                    static_cast<LONG>(RetentionProfilerSegmentState::Sealing),
                    static_cast<LONG>(RetentionProfilerSegmentState::Sealing))
                    == static_cast<LONG>(RetentionProfilerSegmentState::Sealing))
            {
                MemoryBarrier();
                InterlockedExchange(
                    &header->segmentState,
                    static_cast<LONG>(RetentionProfilerSegmentState::Published));
            }
        }

        /// <summary>
        /// 二次确认已登记 writer 仍属于调用方观察到的 Writing 代；失败时撤销登记。
        /// </summary>
        /// <param name="header">已经登记 writer 的共享段。</param>
        /// <param name="observedPublicationSequence">首次观察 Writing 时对应的发布序列。</param>
        /// <returns>登记仍可作为当前代写入租约时返回真。</returns>
        inline bool ConfirmRetentionProfilerSegmentWriter(
            RetentionProfilerSharedHeader* header,
            LONGLONG observedPublicationSequence) noexcept
        {
            if (InterlockedCompareExchange(
                    &header->segmentState,
                    static_cast<LONG>(RetentionProfilerSegmentState::Writing),
                    static_cast<LONG>(RetentionProfilerSegmentState::Writing))
                == static_cast<LONG>(RetentionProfilerSegmentState::Writing)
                && InterlockedCompareExchange64(&header->publicationSequence, 0, 0)
                    == observedPublicationSequence)
            {
                return true;
            }

            ReleaseRetentionProfilerSegmentWriter(header);
            return false;
        }
    }

    /// <summary>
    /// 尝试进入一个 Writing 段；二次状态检查保证发布者不会遗漏尚未完成的固定宽度写入。
    /// </summary>
    /// <param name="header">要写入的共享段头部。</param>
    /// <returns>调用方取得一次写入租约时返回真；返回假时不得触碰 payload。</returns>
    inline bool TryBeginRetentionProfilerSegmentWrite(RetentionProfilerSharedHeader* header) noexcept
    {
        if (header == nullptr)
        {
            return false;
        }

        const LONGLONG observedPublicationSequence = InterlockedCompareExchange64(
            &header->publicationSequence,
            0,
            0);
        if (InterlockedCompareExchange(
                &header->segmentState,
                static_cast<LONG>(RetentionProfilerSegmentState::Writing),
                static_cast<LONG>(RetentionProfilerSegmentState::Writing))
            != static_cast<LONG>(RetentionProfilerSegmentState::Writing))
        {
            return false;
        }

        if (!RetentionProfilerProtocolDetail::TryRegisterRetentionProfilerSegmentWriter(header))
        {
            return false;
        }
        return RetentionProfilerProtocolDetail::ConfirmRetentionProfilerSegmentWriter(
            header,
            observedPublicationSequence);
    }

    /// <summary>
    /// 将 Writing 段封存为指定序列；已有写入者退出前保持 Sealing，最后一个写入者负责 release 发布。
    /// </summary>
    /// <param name="header">要封存的共享段。</param>
    /// <param name="nextPublicationSequence">会话级单调序列计数器；仅取得发布权的调用方递增。</param>
    /// <returns>当前调用成功取得唯一发布权时返回真。</returns>
    inline bool TryPublishRetentionProfilerSegment(
        RetentionProfilerSharedHeader* header,
        volatile LONGLONG* nextPublicationSequence) noexcept
    {
        if (header == nullptr || nextPublicationSequence == nullptr
            || InterlockedCompareExchange(
                &header->segmentState,
                static_cast<LONG>(RetentionProfilerSegmentState::AssigningSequence),
                static_cast<LONG>(RetentionProfilerSegmentState::Writing))
                != static_cast<LONG>(RetentionProfilerSegmentState::Writing))
        {
            return false;
        }

        InterlockedOr(
            &header->activeWriterCount,
            RetentionProfilerProtocolDetail::kWriterRegistrationClosed);
        const LONGLONG publicationSequence = InterlockedIncrement64(nextPublicationSequence);
        InterlockedExchange64(&header->publicationSequence, publicationSequence);
        MemoryBarrier();
        InterlockedExchange(&header->segmentState, static_cast<LONG>(RetentionProfilerSegmentState::Sealing));
        if ((InterlockedCompareExchange(&header->activeWriterCount, 0, 0)
                & RetentionProfilerProtocolDetail::kWriterCountMask)
            == 0)
        {
            MemoryBarrier();
            InterlockedExchange(&header->segmentState, static_cast<LONG>(RetentionProfilerSegmentState::Published));
        }
        return true;
    }

    /// <summary>
    /// 完成一次段内固定宽度写入；最后一个写入者在 Sealing 状态下建立 Published release 屏障。
    /// </summary>
    /// <param name="header">持有写入租约的共享段。</param>
    inline void CompleteRetentionProfilerSegmentWrite(RetentionProfilerSharedHeader* header) noexcept
    {
        if (header == nullptr)
        {
            return;
        }
        RetentionProfilerProtocolDetail::ReleaseRetentionProfilerSegmentWriter(header);
    }

    /// <summary>
    /// 在完整转存后确认精确发布序列；错误或陈旧序列不能让段进入 Reusable，从而阻止 ABA 复用。
    /// </summary>
    /// <param name="header">已发布的共享段。</param>
    /// <param name="publicationSequence">Controller 实际完整落盘的发布序列。</param>
    /// <returns>确认与当前发布匹配并成功归还段时返回真。</returns>
    inline bool TryAcknowledgeRetentionProfilerSegment(
        RetentionProfilerSharedHeader* header,
        LONGLONG publicationSequence) noexcept
    {
        if (header == nullptr
            || InterlockedCompareExchange(
                &header->segmentState,
                static_cast<LONG>(RetentionProfilerSegmentState::Published),
                static_cast<LONG>(RetentionProfilerSegmentState::Published))
                != static_cast<LONG>(RetentionProfilerSegmentState::Published)
            || InterlockedCompareExchange64(&header->publicationSequence, 0, 0) != publicationSequence)
        {
            return false;
        }

        InterlockedExchange64(&header->acknowledgedSequence, publicationSequence);
        MemoryBarrier();
        return InterlockedCompareExchange(
            &header->segmentState,
            static_cast<LONG>(RetentionProfilerSegmentState::Reusable),
            static_cast<LONG>(RetentionProfilerSegmentState::Published))
            == static_cast<LONG>(RetentionProfilerSegmentState::Published);
    }

    /// <summary>
    /// Profiler 在 ObjectReferences 回调中写入的原始对象记录；ClassID 在冻结后解析类型名称，大小来自 CLR GetObjectSize。
    /// </summary>
    struct RetentionProfilerObjectRecord final
    {
        UINT_PTR objectId;
        UINT_PTR classId;
        UINT_PTR sizeBytes;
    };

    /// <summary>
    /// Profiler 在 ObjectReferences 回调中写入的原始对象引用边。
    /// </summary>
    struct RetentionProfilerEdgeRecord final
    {
        UINT_PTR sourceObjectId;
        UINT_PTR targetObjectId;
    };

    /// <summary>
    /// Profiler 在 RootReferences2 回调中写入的原始 GC 根；栈根的 rootId 是 CLR FunctionID。
    /// </summary>
    struct RetentionProfilerRootRecord final
    {
        UINT_PTR objectId;
        std::uint32_t rootKind;
        std::uint32_t rootFlags;
        UINT_PTR rootId;
        std::uint32_t functionEvidenceIndex;
        std::uint32_t reserved;
    };

    /// <summary>
    /// 在 GC 完成后通过 CLR Metadata API 解析的有限函数证据；字符串使用固定缓冲区避免回调内无界分配。
    /// </summary>
    struct RetentionProfilerFunctionEvidenceRecord final
    {
        UINT_PTR functionId;
        wchar_t functionName[512];
        wchar_t moduleName[260];
    };

    /// <summary>
    /// 在 GC 图冻结后通过 CLR Metadata API 解析的有限类型证据；ClassID 与固定 UTF-16 名称共同形成可信映射。
    /// </summary>
    /// <remarks>
    /// Profiler 只在预分配共享内存中写入该记录。无法解析的运行时类型不生成记录，调用方不得据此伪造名称。
    /// </remarks>
    struct RetentionProfilerTypeEvidenceRecord final
    {
        /// <summary>CLR 为当前具体运行时类型提供的 ClassID。</summary>
        UINT_PTR classId;

        /// <summary>完整构造类型名；泛型包含 CLR 类型实参，数组包含元素类型和秩。</summary>
        wchar_t typeName[512];

        /// <summary>定义类型的模块路径；CLR 未提供模块时允许为空。</summary>
        wchar_t moduleName[260];
    };

    /// <summary>
    /// 表示根记录没有已验证函数证据的稳定索引值。
    /// </summary>
    constexpr std::uint32_t kNoFunctionEvidence = UINT32_MAX;

    /// <summary>
    /// 验证固定长度名称缓冲区是否含有终止空字符。
    /// </summary>
    template <std::size_t TLength>
    constexpr bool IsNullTerminated(const wchar_t (&value)[TLength]) noexcept
    {
        for (std::size_t index = 0; index < TLength; ++index)
        {
            if (value[index] == L'\0')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 在调用 CLR 附加 API 前验证客户端数据，拒绝错误版本、过小映射或未终止名称。
    /// </summary>
    constexpr bool IsValidRetentionProfilerAttachData(const RetentionProfilerAttachData& value) noexcept
    {
        if (value.version != kRetentionProfilerProtocolVersion
            || value.segmentCapacityBytes < 64 * 1024
            || value.segmentCount < 2
            || value.segmentCount > kRetentionProfilerMaximumSegmentCount
            || !IsNullTerminated(value.completionEventName)
            || !IsNullTerminated(value.failureEventName)
            || !IsNullTerminated(value.detachEventName))
        {
            return false;
        }

        for (std::uint32_t index = 0; index < value.segmentCount; ++index)
        {
            if (!IsNullTerminated(value.segmentNames[index]))
            {
                return false;
            }
        }

        return true;
    }
}
