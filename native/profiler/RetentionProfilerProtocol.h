#pragma once

#include <windows.h>

#include <cstddef>
#include <cstdint>

namespace DotnetAnalysis
{
    /// <summary>
    /// 共享内存协议的固定版本；Controller 与 Profiler 必须完全一致才允许附加。
    /// </summary>
    constexpr std::uint32_t kRetentionProfilerProtocolVersion = 5;

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

        /// <summary>共享内存总容量，必须包含 <see cref="RetentionProfilerSharedHeader"/>。</summary>
        std::uint32_t mappingCapacityBytes;

        /// <summary>Controller 创建的共享内存命名映射。</summary>
        wchar_t mappingName[260];

        /// <summary>Profiler 完成一次 GC 数据采集后设置的命名事件。</summary>
        wchar_t completionEventName[260];

        /// <summary>Profiler 在协议或容量失败时设置的命名事件。</summary>
        wchar_t failureEventName[260];

        /// <summary>CLR 确认 Profiler 已完成分离时设置的命名事件。</summary>
        wchar_t detachEventName[260];
    };

    /// <summary>
    /// 共享内存内捕获状态；状态只从 Pending 推进到 Capturing、Completed 或 Failed。
    /// </summary>
    enum class RetentionProfilerCaptureStatus : LONG
    {
        /// <summary>Controller 已创建映射，Profiler 尚未开始写入。</summary>
        Pending = 0,

        /// <summary>GC 回调正在向预分配区域写入原始记录。</summary>
        Capturing = 1,

        /// <summary>所有记录已冻结，可由 Controller 读取和落盘。</summary>
        Completed = 2,

        /// <summary>协议无效、记录溢出或 CLR 回调失败，结果不得读取。</summary>
        Failed = 3
    };

    /// <summary>
    /// 共享映射固定头部，计数仅由 Profiler 单调递增，Controller 只在完成事件后读取。
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
    };

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

        /// <summary>元数据类型全名；数组类型使用 CLR 元素类型和秩构造真实数组名称。</summary>
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
        return value.version == kRetentionProfilerProtocolVersion
            && value.mappingCapacityBytes >= 64 * 1024
            && IsNullTerminated(value.mappingName)
            && IsNullTerminated(value.completionEventName)
            && IsNullTerminated(value.failureEventName)
            && IsNullTerminated(value.detachEventName);
    }
}
