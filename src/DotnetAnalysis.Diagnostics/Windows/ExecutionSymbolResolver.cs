using System.Collections.Concurrent;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreSourceLocation = DotnetAnalysis.Core.Diagnostics.SourceLocation;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 说明本地 PDB 源码位置查找的确定结果，供调用方记录而不把普通不可用状态当作异常。
/// </summary>
internal enum ExecutionSourceLocationLookupStatus
{
    Found,
    DynamicModule,
    PdbMissing,
    PdbMismatch,
    SourceUnavailable
}

/// <summary>
/// 携带本地符号查找状态及找到时的构建路径、行号和列号。
/// </summary>
internal sealed record ExecutionSourceLocationLookupResult(
    ExecutionSourceLocationLookupStatus Status,
    string? BuildTimeFilePath,
    int LineNumber,
    int ColumnNumber);

/// <summary>
/// 抽象从执行帧解析源码位置的策略，便于替换符号实现和构造故障测试。
/// </summary>
internal interface IExecutionSourceLocationLookup
{
    /// <summary>
    /// 查找给定执行帧的本地源码位置或返回确定的不可用状态。
    /// </summary>
    ExecutionSourceLocationLookupResult Lookup(ExecutionFrameReference frame);
}

/// <summary>
/// 描述模块嵌入的 PDB 标识，用于拒绝名称相同但签名或年龄不匹配的符号文件。
/// </summary>
internal sealed record ExecutionModuleSymbolIdentity(
    string? PdbName,
    Guid PdbSignature,
    int PdbAge,
    string? FileVersion);

/// <summary>
/// 表示符号读取器返回的构建时源码路径和可选列位置。
/// </summary>
internal sealed record ExecutionSourceLine(
    string? BuildTimeFilePath,
    int LineNumber,
    int ColumnNumber);

/// <summary>
/// 从运行时符号地址提取模块 PDB 标识，隔离 TraceEvent 类型。
/// </summary>
internal interface IExecutionSymbolAddressInspector
{
    /// <summary>
    /// 返回帧所属模块声明的 PDB 标识；缺少运行时符号地址时返回空值。
    /// </summary>
    ExecutionModuleSymbolIdentity? GetModuleIdentity(ExecutionFrameReference frame);
}

/// <summary>
/// 抽象本地路径和文件检查，使符号解析可拒绝非本地路径并可确定性测试。
/// </summary>
internal interface IExecutionLocalFileSystem
{
    /// <summary>
    /// 读取路径根所在驱动器类型，用于区分本地与网络或设备路径。
    /// </summary>
    DriveType GetDriveType(string pathRoot);

    /// <summary>
    /// 检查受路径策略限制的本地文件是否存在。
    /// </summary>
    bool FileExists(string path);
}

/// <summary>
/// 创建仅允许访问指定模块目录的符号读取器。
/// </summary>
internal interface IExecutionSymbolReaderFactory
{
    /// <summary>
    /// 为指定模块目录创建仅接受安全检查允许路径的符号读取器。
    /// </summary>
    IExecutionSymbolReader Create(string moduleDirectory, Func<string, bool> securityCheck);
}

/// <summary>
/// 定义匹配 PDB 与读取执行帧源码行所需的最小符号读取能力。
/// </summary>
internal interface IExecutionSymbolReader : IDisposable
{
    /// <summary>
    /// 按模块完整 PDB 标识返回候选符号文件路径。
    /// </summary>
    string? FindSymbolFilePath(
        string pdbFileName,
        ExecutionModuleSymbolIdentity moduleIdentity,
        string modulePath);

    /// <summary>
    /// 读取执行帧对应的构建时源码行；缺少源码映射时返回空值。
    /// </summary>
    ExecutionSourceLine? GetSourceLine(ExecutionFrameReference frame);
}

/// <summary>
/// 使用 TraceEvent 的模块元数据提取运行时帧所声明的 PDB 标识。
/// </summary>
internal sealed class TraceEventExecutionSymbolAddressInspector : IExecutionSymbolAddressInspector
{
    /// <summary>
    /// 从 TraceEvent 模块文件提取 PDB 名称、签名、年龄和文件版本。
    /// </summary>
    public ExecutionModuleSymbolIdentity? GetModuleIdentity(ExecutionFrameReference frame)
    {
        var moduleFile = frame.SymbolAddress?.ModuleFile;
        return moduleFile is null
            ? null
            : new ExecutionModuleSymbolIdentity(
                moduleFile.PdbName,
                moduleFile.PdbSignature,
                moduleFile.PdbAge,
                moduleFile.FileVersion);
    }
}

/// <summary>
/// 以系统文件 API 实现本地驱动器类型和文件存在性检查。
/// </summary>
internal sealed class SystemExecutionLocalFileSystem : IExecutionLocalFileSystem
{
    /// <summary>
    /// 返回指定本地路径根的驱动器类型。
    /// </summary>
    public DriveType GetDriveType(string pathRoot) => new DriveInfo(pathRoot).DriveType;

    /// <summary>
    /// 使用系统文件 API 检查文件存在性。
    /// </summary>
    public bool FileExists(string path) => File.Exists(path);
}

/// <summary>
/// 创建配置为本地缓存模式、禁用符号下载的 TraceEvent 符号读取器。
/// </summary>
internal sealed class TraceEventExecutionSymbolReaderFactory : IExecutionSymbolReaderFactory
{
    /// <summary>
    /// 为模块目录创建带路径安全回调的读取器，并封装为诊断层内部契约。
    /// </summary>
    public IExecutionSymbolReader Create(string moduleDirectory, Func<string, bool> securityCheck)
    {
        var reader = CreateConfiguredReader(moduleDirectory, securityCheck);
        return new TraceEventExecutionSymbolReader(reader);
    }

    /// <summary>
    /// 配置 TraceEvent 读取器为仅查找本地缓存，禁止生成 NGen 符号或探测远程符号源。
    /// </summary>
    internal static SymbolReader CreateConfiguredReader(
        string moduleDirectory,
        Func<string, bool> securityCheck)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentNullException.ThrowIfNull(securityCheck);

        return new SymbolReader(TextWriter.Null, moduleDirectory)
        {
            Options = SymbolReaderOptions.CacheOnly | SymbolReaderOptions.NoNGenSymbolCreation,
            SourcePath = string.Empty,
            SecurityCheck = securityCheck
        };
    }

    /// <summary>
    /// 将 TraceEvent 的 <see cref="SymbolReader"/> 适配为仅暴露本项目需要的符号读取操作。
    /// </summary>
    private sealed class TraceEventExecutionSymbolReader : IExecutionSymbolReader
    {
        private readonly SymbolReader _reader;

        /// <summary>
        /// 包装已按本地安全策略配置的 TraceEvent 符号读取器。
        /// </summary>
        public TraceEventExecutionSymbolReader(SymbolReader reader)
        {
            _reader = reader;
        }

        /// <summary>
        /// 根据模块完整 PDB 标识查找匹配符号文件，而不是仅按文件名接受候选文件。
        /// </summary>
        public string? FindSymbolFilePath(
            string pdbFileName,
            ExecutionModuleSymbolIdentity moduleIdentity,
            string modulePath) =>
            _reader.FindSymbolFilePath(
                pdbFileName,
                moduleIdentity.PdbSignature,
                moduleIdentity.PdbAge,
                modulePath,
                moduleIdentity.FileVersion,
                portablePdbMatch: true);

        /// <summary>
        /// 将 TraceEvent 源码行投影为不泄漏 SDK 类型的内部值对象。
        /// </summary>
        public ExecutionSourceLine? GetSourceLine(ExecutionFrameReference frame)
        {
            TraceCodeAddress? symbolAddress = frame.SymbolAddress;
            var sourceLocation = symbolAddress?.GetSourceLine(_reader);
            return sourceLocation is null
                ? null
                : new ExecutionSourceLine(
                    sourceLocation.SourceFile.BuildTimeFilePath,
                    sourceLocation.LineNumber,
                    sourceLocation.ColumnNumber);
        }

        /// <summary>
        /// 释放底层符号读取器及其本地文件句柄。
        /// </summary>
        public void Dispose() => _reader.Dispose();
    }
}

/// <summary>
/// 统一限制符号与源码解析只能访问本机本地卷上的完整路径，避免网络或设备路径触发非预期 I/O。
/// </summary>
internal sealed class ExecutionLocalPathPolicy
{
    private readonly IExecutionLocalFileSystem _fileSystem;

    /// <summary>
    /// 使用可替换的文件系统边界创建路径策略。
    /// </summary>
    public ExecutionLocalPathPolicy(IExecutionLocalFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>
    /// 仅当路径是已规范化的本机本地卷绝对路径时返回其完整形式；UNC、设备和远程卷一律拒绝。
    /// </summary>
    public bool TryGetLocalFullPath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(path);
            if (candidate.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return false;
            }

            var pathRoot = Path.GetPathRoot(candidate);
            if (string.IsNullOrWhiteSpace(pathRoot)
                || pathRoot.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return false;
            }

            var driveType = _fileSystem.GetDriveType(pathRoot);
            if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 在通过本地路径策略后检查文件存在性，并将文件系统访问失败安全降级为不可用。
    /// </summary>
    public bool TryGetExistingLocalFile(string? path, out string fullPath)
    {
        if (!TryGetLocalFullPath(path, out fullPath))
        {
            return false;
        }

        try
        {
            return _fileSystem.FileExists(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            fullPath = string.Empty;
            return false;
        }
    }
}

/// <summary>
/// 按会话帧标识单飞解析本地匹配 PDB 中的源代码位置。
/// </summary>
internal sealed class ExecutionSymbolResolver : IAsyncDisposable
{
    private static readonly Action<ILogger, int, ExecutionSourceLocationLookupStatus, Exception?> s_sourceUnavailable =
        LoggerMessage.Define<int, ExecutionSourceLocationLookupStatus>(
            LogLevel.Debug,
            new EventId(1, "ExecutionSourceUnavailable"),
            "Source location is unavailable for execution frame {FrameId}: {Status}.");

    private static readonly Action<ILogger, int, Exception?> s_sourceResolutionFailed =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(2, "ExecutionSourceResolutionFailed"),
            "Source location resolution failed for execution frame {FrameId}.");

    private readonly ConcurrentDictionary<int, Task<CoreSourceLocation?>> _sourceLocations = [];
    private readonly IExecutionSourceLocationLookup _lookup;
    private readonly ILogger<ExecutionSymbolResolver> _logger;
    private readonly ExecutionLocalPathPolicy _localPathPolicy;
    private readonly object _lifecycleLock = new();
    private int _disposed;

    /// <summary>
    /// 创建只使用模块目录内本地 PDB 的符号解析器。
    /// </summary>
    internal ExecutionSymbolResolver(
        IExecutionSourceLocationLookup? lookup = null,
        ILogger<ExecutionSymbolResolver>? logger = null,
        IExecutionLocalFileSystem? fileSystem = null,
        IExecutionSymbolReaderFactory? symbolReaderFactory = null,
        IExecutionSymbolAddressInspector? symbolAddressInspector = null)
    {
        _localPathPolicy = new ExecutionLocalPathPolicy(
            fileSystem ?? new SystemExecutionLocalFileSystem());
        _lookup = lookup ?? new LocalPdbSourceLocationLookup(
            _localPathPolicy,
            symbolReaderFactory ?? new TraceEventExecutionSymbolReaderFactory(),
            symbolAddressInspector ?? new TraceEventExecutionSymbolAddressInspector());
        _logger = logger ?? NullLogger<ExecutionSymbolResolver>.Instance;
    }

    /// <summary>
    /// 解析一个帧的源代码位置；调用方取消只取消自身等待。
    /// </summary>
    public Task<CoreSourceLocation?> ResolveAsync(
        ExecutionFrameReference frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegative(frame.FrameId);
        cancellationToken.ThrowIfCancellationRequested();

        Task<CoreSourceLocation?> sharedResolution;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var completion = new TaskCompletionSource<CoreSourceLocation?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            sharedResolution = _sourceLocations.GetOrAdd(frame.FrameId, completion.Task);
            if (ReferenceEquals(sharedResolution, completion.Task))
            {
                _ = ResolveAndCompleteAsync(frame, completion);
            }
        }

        return cancellationToken.CanBeCanceled
            ? sharedResolution.WaitAsync(cancellationToken)
            : sharedResolution;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task<CoreSourceLocation?>[] pendingResolutions;
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            pendingResolutions = _sourceLocations.Values.ToArray();
        }

        await Task.WhenAll(pendingResolutions).ConfigureAwait(false);
        if (_lookup is IDisposable disposableLookup)
        {
            disposableLookup.Dispose();
        }

        _sourceLocations.Clear();
    }

    /// <summary>
    /// 在线程池执行单帧解析并总是完成共享任务，使一个调用方的异常不会遗留等待者。
    /// </summary>
    private async Task ResolveAndCompleteAsync(
        ExecutionFrameReference frame,
        TaskCompletionSource<CoreSourceLocation?> completion)
    {
        CoreSourceLocation? sourceLocation = null;
        try
        {
            sourceLocation = await Task.Run(
                () => ResolveCore(frame),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogResolutionFailure(frame.FrameId, exception);
        }
        finally
        {
            completion.TrySetResult(sourceLocation);
        }
    }

    /// <summary>
    /// 根据本地 PDB 查找结果生成领域源码位置；普通不可用和解析异常均返回空值并记录诊断。
    /// </summary>
    private CoreSourceLocation? ResolveCore(ExecutionFrameReference frame)
    {
        try
        {
            var result = _lookup.Lookup(frame);
            if (result.Status is not ExecutionSourceLocationLookupStatus.Found)
            {
                LogSourceUnavailable(frame.FrameId, result.Status);
                return null;
            }

            if (string.IsNullOrWhiteSpace(result.BuildTimeFilePath)
                || result.LineNumber <= 0
                || !_localPathPolicy.TryGetExistingLocalFile(result.BuildTimeFilePath, out var sourcePath))
            {
                LogSourceUnavailable(frame.FrameId, ExecutionSourceLocationLookupStatus.SourceUnavailable);
                return null;
            }

            int? columnNumber = result.ColumnNumber > 0 ? result.ColumnNumber : null;
            return new CoreSourceLocation(sourcePath, result.LineNumber, columnNumber);
        }
        catch (Exception exception)
        {
            LogResolutionFailure(frame.FrameId, exception);
            return null;
        }
    }

    /// <summary>
    /// 尽力记录正常的源码不可用原因，日志实现异常不得影响执行分析。
    /// </summary>
    private void LogSourceUnavailable(int frameId, ExecutionSourceLocationLookupStatus status)
    {
        try
        {
            s_sourceUnavailable(_logger, frameId, status, null);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 尽力记录意外的符号解析故障，日志异常不得打破共享单飞任务。
    /// </summary>
    private void LogResolutionFailure(int frameId, Exception exception)
    {
        try
        {
            s_sourceResolutionFailed(_logger, frameId, exception);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 仅使用模块同目录、PDB 标识精确匹配的本地符号文件解析源码，并按模块目录复用读取器。
    /// </summary>
    private sealed class LocalPdbSourceLocationLookup : IExecutionSourceLocationLookup, IDisposable
    {
        private readonly object _readersLock = new();
        private readonly ExecutionLocalPathPolicy _localPathPolicy;
        private readonly IExecutionSymbolReaderFactory _symbolReaderFactory;
        private readonly IExecutionSymbolAddressInspector _symbolAddressInspector;
        private readonly Dictionary<string, SymbolReaderLease> _readers = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        /// <summary>
        /// 组合路径策略、读取器工厂与模块标识检查器，所有依赖均可替换以覆盖安全边界。
        /// </summary>
        public LocalPdbSourceLocationLookup(
            ExecutionLocalPathPolicy localPathPolicy,
            IExecutionSymbolReaderFactory symbolReaderFactory,
            IExecutionSymbolAddressInspector symbolAddressInspector)
        {
            _localPathPolicy = localPathPolicy;
            _symbolReaderFactory = symbolReaderFactory;
            _symbolAddressInspector = symbolAddressInspector;
        }

        /// <summary>
        /// 验证模块和 PDB 均为本地且精确匹配后，读取帧的构建时源码位置。
        /// </summary>
        public ExecutionSourceLocationLookupResult Lookup(ExecutionFrameReference frame)
        {
            var modulePath = frame.Descriptor.ModulePath;
            if (string.IsNullOrWhiteSpace(modulePath)
                || !_localPathPolicy.TryGetLocalFullPath(modulePath, out var fullModulePath))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.DynamicModule);
            }

            var moduleIdentity = _symbolAddressInspector.GetModuleIdentity(frame);
            var pdbName = moduleIdentity?.PdbName;
            if (moduleIdentity is null || string.IsNullOrWhiteSpace(pdbName))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMissing);
            }

            if (moduleIdentity.PdbSignature == Guid.Empty || moduleIdentity.PdbAge <= 0)
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMismatch);
            }

            if (Path.IsPathFullyQualified(pdbName)
                && !_localPathPolicy.TryGetLocalFullPath(pdbName, out _))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMissing);
            }

            var moduleDirectory = Path.GetDirectoryName(fullModulePath);
            var pdbFileName = Path.GetFileName(pdbName);
            if (string.IsNullOrWhiteSpace(moduleDirectory) || string.IsNullOrWhiteSpace(pdbFileName))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMissing);
            }

            var pdbPath = Path.Combine(moduleDirectory, pdbFileName);
            if (!_localPathPolicy.TryGetExistingLocalFile(pdbPath, out var fullPdbPath))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMissing);
            }

            var lease = GetOrAddReader(moduleDirectory);
            lock (lease.SyncRoot)
            {
                var matchedPdbPath = lease.Reader.FindSymbolFilePath(
                    pdbFileName,
                    moduleIdentity,
                    fullModulePath);
                if (!_localPathPolicy.TryGetLocalFullPath(matchedPdbPath, out var fullMatchedPdbPath)
                    || !string.Equals(fullMatchedPdbPath, fullPdbPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Unavailable(ExecutionSourceLocationLookupStatus.PdbMismatch);
                }

                var sourceLocation = lease.Reader.GetSourceLine(frame);
                return sourceLocation is null
                    ? Unavailable(ExecutionSourceLocationLookupStatus.SourceUnavailable)
                    : new ExecutionSourceLocationLookupResult(
                        ExecutionSourceLocationLookupStatus.Found,
                        sourceLocation.BuildTimeFilePath,
                        sourceLocation.LineNumber,
                        sourceLocation.ColumnNumber);
            }
        }

        /// <summary>
        /// 停止创建新读取器并释放所有按模块目录缓存的符号读取器。
        /// </summary>
        public void Dispose()
        {
            SymbolReaderLease[] readers;
            lock (_readersLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                readers = _readers.Values.ToArray();
                _readers.Clear();
            }

            foreach (var reader in readers)
            {
                reader.Reader.Dispose();
            }
        }

        /// <summary>
        /// 获取模块目录唯一读取器；创建时将读取器安全检查限制为同一目录。
        /// </summary>
        private SymbolReaderLease GetOrAddReader(string moduleDirectory)
        {
            lock (_readersLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_readers.TryGetValue(moduleDirectory, out var existingReader))
                {
                    return existingReader;
                }

                var reader = _symbolReaderFactory.Create(
                    moduleDirectory,
                    path => IsFileInDirectory(path, moduleDirectory));
                var lease = new SymbolReaderLease(reader);
                _readers.Add(moduleDirectory, lease);
                return lease;
            }
        }

        /// <summary>
        /// 判断符号读取器请求的路径是否经本地化后仍恰好位于允许模块目录内。
        /// </summary>
        private bool IsFileInDirectory(string path, string directory)
        {
            if (!_localPathPolicy.TryGetLocalFullPath(path, out var fullPath))
            {
                return false;
            }

            var containingDirectory = Path.GetDirectoryName(fullPath);
            return string.Equals(containingDirectory, directory, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 为普通本地符号不可用情况创建无路径、零坐标的确定性结果。
        /// </summary>
        private static ExecutionSourceLocationLookupResult Unavailable(
            ExecutionSourceLocationLookupStatus status) =>
            new(status, null, LineNumber: 0, ColumnNumber: 0);

        /// <summary>
        /// 将目录缓存的读取器与其串行访问锁绑定，避免 SymbolReader 并发读取竞态。
        /// </summary>
        private sealed record SymbolReaderLease(IExecutionSymbolReader Reader)
        {
            public object SyncRoot { get; } = new();
        }
    }
}
