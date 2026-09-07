using System.Collections.Concurrent;
using Microsoft.Diagnostics.Symbols;
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
    private readonly object _lifecycleLock = new();
    private int _disposed;

    /// <summary>
    /// 创建只使用模块目录内本地 PDB 的符号解析器。
    /// </summary>
    internal ExecutionSymbolResolver(
        IExecutionSourceLocationLookup? lookup = null,
        ILogger<ExecutionSymbolResolver>? logger = null)
    {
        _lookup = lookup ?? new LocalPdbSourceLocationLookup();
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
        var sourceLocation = await Task.Run(
            () => ResolveCore(frame),
            CancellationToken.None).ConfigureAwait(false);
        completion.TrySetResult(sourceLocation);
    }

    private CoreSourceLocation? ResolveCore(ExecutionFrameReference frame)
    {
        try
        {
            var result = _lookup.Lookup(frame);
            if (result.Status is not ExecutionSourceLocationLookupStatus.Found)
            {
                s_sourceUnavailable(_logger, frame.FrameId, result.Status, null);
                return null;
            }

            if (string.IsNullOrWhiteSpace(result.BuildTimeFilePath)
                || result.LineNumber <= 0
                || !File.Exists(result.BuildTimeFilePath))
            {
                s_sourceUnavailable(
                    _logger,
                    frame.FrameId,
                    ExecutionSourceLocationLookupStatus.SourceUnavailable,
                    null);
                return null;
            }

            int? columnNumber = result.ColumnNumber > 0 ? result.ColumnNumber : null;
            return new CoreSourceLocation(result.BuildTimeFilePath, result.LineNumber, columnNumber);
        }
        catch (Exception exception)
        {
            s_sourceResolutionFailed(_logger, frame.FrameId, exception);
            return null;
        }
    }

    private sealed class LocalPdbSourceLocationLookup : IExecutionSourceLocationLookup, IDisposable
    {
        private readonly object _readersLock = new();
        private readonly Dictionary<string, SymbolReaderLease> _readers = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ExecutionSourceLocationLookupResult Lookup(ExecutionFrameReference frame)
        {
            var symbolAddress = frame.SymbolAddress;
            var modulePath = frame.Descriptor.ModulePath;
            if (symbolAddress is null || string.IsNullOrWhiteSpace(modulePath))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.DynamicModule);
            }

            var moduleFile = symbolAddress.ModuleFile;
            if (moduleFile is null || string.IsNullOrWhiteSpace(moduleFile.PdbName))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMissing);
            }

            if (moduleFile.PdbSignature == Guid.Empty || moduleFile.PdbAge <= 0)
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMismatch);
            }

            var fullModulePath = Path.GetFullPath(modulePath);
            var moduleDirectory = Path.GetDirectoryName(fullModulePath);
            var pdbFileName = Path.GetFileName(moduleFile.PdbName);
            if (string.IsNullOrWhiteSpace(moduleDirectory) || string.IsNullOrWhiteSpace(pdbFileName))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMissing);
            }

            var pdbPath = Path.Combine(moduleDirectory, pdbFileName);
            if (!File.Exists(pdbPath))
            {
                return Unavailable(ExecutionSourceLocationLookupStatus.PdbMissing);
            }

            var lease = GetOrAddReader(moduleDirectory);
            lock (lease.SyncRoot)
            {
                var matchedPdbPath = lease.Reader.FindSymbolFilePath(
                    pdbFileName,
                    moduleFile.PdbSignature,
                    moduleFile.PdbAge,
                    fullModulePath,
                    moduleFile.FileVersion,
                    portablePdbMatch: true);
                if (!string.Equals(
                        Path.GetFullPath(matchedPdbPath ?? string.Empty),
                        Path.GetFullPath(pdbPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Unavailable(ExecutionSourceLocationLookupStatus.PdbMismatch);
                }

                var sourceLocation = symbolAddress.GetSourceLine(lease.Reader);
                return sourceLocation is null
                    ? Unavailable(ExecutionSourceLocationLookupStatus.SourceUnavailable)
                    : new ExecutionSourceLocationLookupResult(
                        ExecutionSourceLocationLookupStatus.Found,
                        sourceLocation.SourceFile.BuildTimeFilePath,
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

                var reader = new SymbolReader(TextWriter.Null, moduleDirectory)
                {
                    Options = SymbolReaderOptions.CacheOnly | SymbolReaderOptions.NoNGenSymbolCreation,
                    SourcePath = string.Empty,
                    SecurityCheck = path => IsFileInDirectory(path, moduleDirectory)
                };
                var lease = new SymbolReaderLease(reader);
                _readers.Add(moduleDirectory, lease);
                return lease;
            }
        }

        private static bool IsFileInDirectory(string path, string directory)
        {
            var fullPath = Path.GetFullPath(path);
            var containingDirectory = Path.GetDirectoryName(fullPath);
            return string.Equals(containingDirectory, directory, StringComparison.OrdinalIgnoreCase);
        }

        private static ExecutionSourceLocationLookupResult Unavailable(
            ExecutionSourceLocationLookupStatus status) =>
            new(status, null, LineNumber: 0, ColumnNumber: 0);

        private sealed record SymbolReaderLease(SymbolReader Reader)
        {
            public object SyncRoot { get; } = new();
        }
    }
}
