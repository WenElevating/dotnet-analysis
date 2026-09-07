using System.Collections.Concurrent;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreSourceLocation = DotnetAnalysis.Core.Diagnostics.SourceLocation;

namespace DotnetAnalysis.Diagnostics.Windows;

internal enum ExecutionSourceLocationLookupStatus
{
    Found,
    DynamicModule,
    PdbMissing,
    PdbMismatch,
    SourceUnavailable
}

internal sealed record ExecutionSourceLocationLookupResult(
    ExecutionSourceLocationLookupStatus Status,
    string? BuildTimeFilePath,
    int LineNumber,
    int ColumnNumber);

internal interface IExecutionSourceLocationLookup
{
    ExecutionSourceLocationLookupResult Lookup(ExecutionFrameReference frame);
}

internal sealed record ExecutionModuleSymbolIdentity(
    string? PdbName,
    Guid PdbSignature,
    int PdbAge,
    string? FileVersion);

internal sealed record ExecutionSourceLine(
    string? BuildTimeFilePath,
    int LineNumber,
    int ColumnNumber);

internal interface IExecutionSymbolAddressInspector
{
    ExecutionModuleSymbolIdentity? GetModuleIdentity(ExecutionFrameReference frame);
}

internal interface IExecutionLocalFileSystem
{
    DriveType GetDriveType(string pathRoot);

    bool FileExists(string path);
}

internal interface IExecutionSymbolReaderFactory
{
    IExecutionSymbolReader Create(string moduleDirectory, Func<string, bool> securityCheck);
}

internal interface IExecutionSymbolReader : IDisposable
{
    string? FindSymbolFilePath(
        string pdbFileName,
        ExecutionModuleSymbolIdentity moduleIdentity,
        string modulePath);

    ExecutionSourceLine? GetSourceLine(ExecutionFrameReference frame);
}

internal sealed class TraceEventExecutionSymbolAddressInspector : IExecutionSymbolAddressInspector
{
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

internal sealed class SystemExecutionLocalFileSystem : IExecutionLocalFileSystem
{
    public DriveType GetDriveType(string pathRoot) => new DriveInfo(pathRoot).DriveType;

    public bool FileExists(string path) => File.Exists(path);
}

internal sealed class TraceEventExecutionSymbolReaderFactory : IExecutionSymbolReaderFactory
{
    public IExecutionSymbolReader Create(string moduleDirectory, Func<string, bool> securityCheck)
    {
        var reader = new SymbolReader(TextWriter.Null, moduleDirectory)
        {
            Options = SymbolReaderOptions.CacheOnly | SymbolReaderOptions.NoNGenSymbolCreation,
            SourcePath = string.Empty,
            SecurityCheck = securityCheck
        };
        return new TraceEventExecutionSymbolReader(reader);
    }

    private sealed class TraceEventExecutionSymbolReader : IExecutionSymbolReader
    {
        private readonly SymbolReader _reader;

        public TraceEventExecutionSymbolReader(SymbolReader reader)
        {
            _reader = reader;
        }

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

        public void Dispose() => _reader.Dispose();
    }
}

internal sealed class ExecutionLocalPathPolicy
{
    private readonly IExecutionLocalFileSystem _fileSystem;

    public ExecutionLocalPathPolicy(IExecutionLocalFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

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

    private sealed class LocalPdbSourceLocationLookup : IExecutionSourceLocationLookup, IDisposable
    {
        private readonly object _readersLock = new();
        private readonly ExecutionLocalPathPolicy _localPathPolicy;
        private readonly IExecutionSymbolReaderFactory _symbolReaderFactory;
        private readonly IExecutionSymbolAddressInspector _symbolAddressInspector;
        private readonly Dictionary<string, SymbolReaderLease> _readers = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public LocalPdbSourceLocationLookup(
            ExecutionLocalPathPolicy localPathPolicy,
            IExecutionSymbolReaderFactory symbolReaderFactory,
            IExecutionSymbolAddressInspector symbolAddressInspector)
        {
            _localPathPolicy = localPathPolicy;
            _symbolReaderFactory = symbolReaderFactory;
            _symbolAddressInspector = symbolAddressInspector;
        }

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

        private bool IsFileInDirectory(string path, string directory)
        {
            if (!_localPathPolicy.TryGetLocalFullPath(path, out var fullPath))
            {
                return false;
            }

            var containingDirectory = Path.GetDirectoryName(fullPath);
            return string.Equals(containingDirectory, directory, StringComparison.OrdinalIgnoreCase);
        }

        private static ExecutionSourceLocationLookupResult Unavailable(
            ExecutionSourceLocationLookupStatus status) =>
            new(status, null, LineNumber: 0, ColumnNumber: 0);

        private sealed record SymbolReaderLease(IExecutionSymbolReader Reader)
        {
            public object SyncRoot { get; } = new();
        }
    }
}
