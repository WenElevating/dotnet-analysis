using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionSymbolResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_ConcurrentRequestsForSameFrame_PerformsOneLookup()
    {
        var sourcePath = CreateTemporarySourceFile();
        try
        {
            var lookup = new ControlledSourceLocationLookup(
                new ExecutionSourceLocationLookupResult(
                    ExecutionSourceLocationLookupStatus.Found,
                    sourcePath,
                    LineNumber: 17,
                    ColumnNumber: 5),
                waitForRelease: true);
            await using var resolver = new ExecutionSymbolResolver(lookup);
            var frame = Frame(frameId: 7);

            var first = resolver.ResolveAsync(frame, CancellationToken.None);
            await lookup.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var second = resolver.ResolveAsync(frame, CancellationToken.None);
            lookup.Release.TrySetResult();

            var locations = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, lookup.CallCount);
            Assert.IsNotNull(locations[0]);
            Assert.AreEqual(Path.GetFullPath(sourcePath), locations[0]!.FilePath);
            Assert.AreEqual(17, locations[0]!.LineNumber);
            Assert.AreEqual(5, locations[0]!.ColumnNumber);
            Assert.AreSame(locations[0], locations[1]);
        }
        finally
        {
            DeleteTemporarySourceFile(sourcePath);
        }
    }

    [TestMethod]
    [DataRow("dynamic-module")]
    [DataRow("pdb-missing")]
    [DataRow("pdb-mismatch")]
    public async Task ResolveAsync_WhenLocalSymbolsAreUnavailable_ReturnsNull(string statusName)
    {
        var status = statusName switch
        {
            "dynamic-module" => ExecutionSourceLocationLookupStatus.DynamicModule,
            "pdb-missing" => ExecutionSourceLocationLookupStatus.PdbMissing,
            "pdb-mismatch" => ExecutionSourceLocationLookupStatus.PdbMismatch,
            _ => throw new ArgumentOutOfRangeException(nameof(statusName), statusName, null)
        };
        var lookup = new ControlledSourceLocationLookup(
            new ExecutionSourceLocationLookupResult(status, null, LineNumber: 0, ColumnNumber: 0));
        await using var resolver = new ExecutionSymbolResolver(lookup);

        var location = await resolver.ResolveAsync(Frame(frameId: 11), CancellationToken.None);

        Assert.IsNull(location);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenBuildTimeSourceFileIsMissing_ReturnsNull()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.cs");
        var lookup = new ControlledSourceLocationLookup(
            new ExecutionSourceLocationLookupResult(
                ExecutionSourceLocationLookupStatus.Found,
                missingPath,
                LineNumber: 23,
                ColumnNumber: 4));
        await using var resolver = new ExecutionSymbolResolver(lookup);

        var location = await resolver.ResolveAsync(Frame(frameId: 13), CancellationToken.None);

        Assert.IsNull(location);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenLineNumberIsNotPositive_ReturnsNull()
    {
        var sourcePath = CreateTemporarySourceFile();
        try
        {
            var lookup = new ControlledSourceLocationLookup(
                new ExecutionSourceLocationLookupResult(
                    ExecutionSourceLocationLookupStatus.Found,
                    sourcePath,
                    LineNumber: 0,
                    ColumnNumber: 4));
            await using var resolver = new ExecutionSymbolResolver(lookup);

            var location = await resolver.ResolveAsync(Frame(frameId: 17), CancellationToken.None);

            Assert.IsNull(location);
        }
        finally
        {
            DeleteTemporarySourceFile(sourcePath);
        }
    }

    [TestMethod]
    public async Task ResolveAsync_WhenLookupThrows_ReturnsNullAndCachesFallback()
    {
        var lookup = new ControlledSourceLocationLookup(new IOException("PDB read failed."));
        await using var resolver = new ExecutionSymbolResolver(lookup);
        var frame = Frame(frameId: 19);

        var first = await resolver.ResolveAsync(frame, CancellationToken.None);
        var second = await resolver.ResolveAsync(frame, CancellationToken.None);

        Assert.IsNull(first);
        Assert.IsNull(second);
        Assert.AreEqual(1, lookup.CallCount);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenOneWaitIsCancelled_DoesNotCancelSharedLookup()
    {
        var sourcePath = CreateTemporarySourceFile();
        try
        {
            var lookup = new ControlledSourceLocationLookup(
                new ExecutionSourceLocationLookupResult(
                    ExecutionSourceLocationLookupStatus.Found,
                    sourcePath,
                    LineNumber: 31,
                    ColumnNumber: 0),
                waitForRelease: true);
            await using var resolver = new ExecutionSymbolResolver(lookup);
            var frame = Frame(frameId: 23);
            using var cancellation = new CancellationTokenSource();

            var cancelledWait = resolver.ResolveAsync(frame, cancellation.Token);
            await lookup.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var survivingWait = resolver.ResolveAsync(frame, CancellationToken.None);
            cancellation.Cancel();

            try
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledWait);
            }
            finally
            {
                lookup.Release.TrySetResult();
            }

            var location = await survivingWait.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsNotNull(location);
            Assert.AreEqual(31, location.LineNumber);
            Assert.IsNull(location.ColumnNumber);
            Assert.AreEqual(1, lookup.CallCount);
        }
        finally
        {
            DeleteTemporarySourceFile(sourcePath);
        }
    }

    [TestMethod]
    public async Task ResolveAsync_WhenLoggerThrows_CompletesSharedResolutionAndDispose()
    {
        var lookup = new ControlledSourceLocationLookup(
            new ExecutionSourceLocationLookupResult(
                ExecutionSourceLocationLookupStatus.DynamicModule,
                null,
                LineNumber: 0,
                ColumnNumber: 0));
        var resolver = new ExecutionSymbolResolver(lookup, new ThrowingLogger());

        var location = await resolver.ResolveAsync(Frame(frameId: 29), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        await resolver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsNull(location);
        Assert.AreEqual(1, lookup.CallCount);
    }

    [TestMethod]
    public async Task ResolveAsync_DefaultLookup_RequiresExactPdbIdentityAndReturnsLocalSource()
    {
        const string modulePath = "C:\\app\\Worker.dll";
        const string pdbPath = "C:\\app\\Worker.pdb";
        const string sourcePath = "C:\\src\\Worker.cs";
        var signature = Guid.NewGuid();
        var fileSystem = new ControlledExecutionLocalFileSystem([pdbPath, sourcePath]);
        var reader = new ControlledExecutionSymbolReader(
            matchedPdbPath: pdbPath,
            sourceLine: new ExecutionSourceLine(sourcePath, LineNumber: 37, ColumnNumber: 9));
        var readerFactory = new ControlledExecutionSymbolReaderFactory(reader);
        var symbolAddressInspector = new ControlledExecutionSymbolAddressInspector(
            new ExecutionModuleSymbolIdentity("Worker.pdb", signature, PdbAge: 4, FileVersion: "1.2.3.4"));
        await using var resolver = new ExecutionSymbolResolver(
            lookup: null,
            logger: null,
            fileSystem: fileSystem,
            symbolReaderFactory: readerFactory,
            symbolAddressInspector: symbolAddressInspector);
        var frame = Frame(frameId: 31, modulePath);

        var location = await resolver.ResolveAsync(frame, CancellationToken.None);

        Assert.IsNotNull(location);
        Assert.AreEqual(Path.GetFullPath(sourcePath), location.FilePath);
        Assert.AreEqual(37, location.LineNumber);
        Assert.AreEqual(9, location.ColumnNumber);
        Assert.AreEqual(Path.GetFullPath("C:\\app"), readerFactory.ModuleDirectory);
        Assert.AreEqual("Worker.pdb", reader.PdbFileName);
        Assert.AreEqual(signature, reader.ModuleIdentity?.PdbSignature);
        Assert.AreEqual(4, reader.ModuleIdentity?.PdbAge);
        Assert.AreEqual(Path.GetFullPath(modulePath), reader.ModulePath);
        Assert.AreEqual(1, reader.SourceLineCallCount);
        Assert.IsNotNull(readerFactory.SecurityCheck);
        Assert.IsTrue(readerFactory.SecurityCheck(pdbPath));
        Assert.IsFalse(readerFactory.SecurityCheck("C:\\other\\Worker.pdb"));
        Assert.IsFalse(readerFactory.SecurityCheck("C:\\app\\nested\\Worker.pdb"));
        Assert.IsFalse(readerFactory.SecurityCheck("\\\\?\\C:\\app\\Worker.pdb"));
        Assert.IsFalse(readerFactory.SecurityCheck("\\\\.\\C:\\app\\Worker.pdb"));
    }

    [TestMethod]
    public void CreateConfiguredReader_AppliesOfflineSettingsAndSecurityCheck()
    {
        var moduleDirectory = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        var allowedPath = Path.Combine(moduleDirectory, "Worker.pdb");
        var deniedPath = Path.Combine(moduleDirectory, "Other.pdb");
        Directory.CreateDirectory(moduleDirectory);

        try
        {
            Func<string, bool> securityCheck =
                path => string.Equals(path, allowedPath, StringComparison.OrdinalIgnoreCase);
            using var reader = TraceEventExecutionSymbolReaderFactory.CreateConfiguredReader(
                moduleDirectory,
                securityCheck);

            Assert.AreEqual(moduleDirectory, reader.SymbolPath);
            Assert.AreEqual(string.Empty, reader.SourcePath);
            Assert.IsTrue(reader.Options.HasFlag(SymbolReaderOptions.CacheOnly));
            Assert.IsTrue(reader.Options.HasFlag(SymbolReaderOptions.NoNGenSymbolCreation));
            Assert.AreSame(securityCheck, reader.SecurityCheck);
            Assert.IsTrue(reader.SecurityCheck(allowedPath));
            Assert.IsFalse(reader.SecurityCheck(deniedPath));
        }
        finally
        {
            Directory.Delete(moduleDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolveAsync_DefaultLookup_WhenPdbIdentityDoesNotMatch_ReturnsNull()
    {
        const string modulePath = "C:\\app\\Worker.dll";
        const string pdbPath = "C:\\app\\Worker.pdb";
        var fileSystem = new ControlledExecutionLocalFileSystem([pdbPath]);
        var reader = new ControlledExecutionSymbolReader(
            matchedPdbPath: "C:\\other\\Worker.pdb",
            sourceLine: null);
        var symbolAddressInspector = new ControlledExecutionSymbolAddressInspector(
            new ExecutionModuleSymbolIdentity("Worker.pdb", Guid.NewGuid(), PdbAge: 1, FileVersion: null));
        await using var resolver = new ExecutionSymbolResolver(
            lookup: null,
            logger: null,
            fileSystem: fileSystem,
            symbolReaderFactory: new ControlledExecutionSymbolReaderFactory(reader),
            symbolAddressInspector: symbolAddressInspector);
        var frame = Frame(frameId: 37, modulePath);

        var location = await resolver.ResolveAsync(frame, CancellationToken.None);

        Assert.IsNull(location);
        Assert.AreEqual(1, reader.FindSymbolCallCount);
        Assert.AreEqual(0, reader.SourceLineCallCount);
    }

    [TestMethod]
    [DataRow("empty-signature")]
    [DataRow("zero-age")]
    public async Task ResolveAsync_DefaultLookup_WhenPdbIdentityIsInvalid_DoesNotProbePdb(string invalidPart)
    {
        var signature = invalidPart == "empty-signature" ? Guid.Empty : Guid.NewGuid();
        var age = invalidPart == "zero-age" ? 0 : 1;
        var fileSystem = new ControlledExecutionLocalFileSystem([]);
        var readerFactory = new ControlledExecutionSymbolReaderFactory(
            new ControlledExecutionSymbolReader("C:\\app\\Worker.pdb", sourceLine: null));
        var symbolAddressInspector = new ControlledExecutionSymbolAddressInspector(
            new ExecutionModuleSymbolIdentity("Worker.pdb", signature, age, FileVersion: null));
        await using var resolver = new ExecutionSymbolResolver(
            lookup: null,
            logger: null,
            fileSystem: fileSystem,
            symbolReaderFactory: readerFactory,
            symbolAddressInspector: symbolAddressInspector);

        var location = await resolver.ResolveAsync(
            Frame(frameId: 39, modulePath: "C:\\app\\Worker.dll"),
            CancellationToken.None);

        Assert.IsNull(location);
        Assert.IsEmpty(fileSystem.ExistenceProbes);
        Assert.AreEqual(0, readerFactory.CreateCallCount);
    }

    [TestMethod]
    [DataRow("\\\\server\\share\\Worker.dll")]
    [DataRow("M:\\Worker.dll")]
    [DataRow("\\\\?\\C:\\app\\Worker.dll")]
    [DataRow("\\\\.\\C:\\app\\Worker.dll")]
    public async Task ResolveAsync_DefaultLookup_WhenModuleIsNonLocalPath_DoesNotProbeFileSystem(string modulePath)
    {
        var fileSystem = new ControlledExecutionLocalFileSystem([], ["M:\\"]);
        var readerFactory = new ControlledExecutionSymbolReaderFactory(
            new ControlledExecutionSymbolReader("C:\\app\\Worker.pdb", sourceLine: null));
        var symbolAddressInspector = new ControlledExecutionSymbolAddressInspector(
            new ExecutionModuleSymbolIdentity("Worker.pdb", Guid.NewGuid(), PdbAge: 1, FileVersion: null));
        await using var resolver = new ExecutionSymbolResolver(
            lookup: null,
            logger: null,
            fileSystem: fileSystem,
            symbolReaderFactory: readerFactory,
            symbolAddressInspector: symbolAddressInspector);
        var frame = Frame(frameId: 41, modulePath);

        var location = await resolver.ResolveAsync(frame, CancellationToken.None);

        Assert.IsNull(location);
        Assert.IsEmpty(fileSystem.ExistenceProbes);
        Assert.AreEqual(0, readerFactory.CreateCallCount);
    }

    [TestMethod]
    [DataRow("\\\\server\\share\\Worker.pdb")]
    [DataRow("M:\\Worker.pdb")]
    [DataRow("\\\\?\\C:\\app\\Worker.pdb")]
    [DataRow("\\\\.\\C:\\app\\Worker.pdb")]
    public async Task ResolveAsync_DefaultLookup_WhenPdbMetadataIsNonLocalPath_DoesNotProbePdb(string pdbMetadataPath)
    {
        var fileSystem = new ControlledExecutionLocalFileSystem([], ["M:\\"]);
        var readerFactory = new ControlledExecutionSymbolReaderFactory(
            new ControlledExecutionSymbolReader("C:\\app\\Worker.pdb", sourceLine: null));
        var symbolAddressInspector = new ControlledExecutionSymbolAddressInspector(
            new ExecutionModuleSymbolIdentity(pdbMetadataPath, Guid.NewGuid(), PdbAge: 1, FileVersion: null));
        await using var resolver = new ExecutionSymbolResolver(
            lookup: null,
            logger: null,
            fileSystem: fileSystem,
            symbolReaderFactory: readerFactory,
            symbolAddressInspector: symbolAddressInspector);
        var frame = Frame(frameId: 43, modulePath: "C:\\app\\Worker.dll");

        var location = await resolver.ResolveAsync(frame, CancellationToken.None);

        Assert.IsNull(location);
        Assert.IsEmpty(fileSystem.ExistenceProbes);
        Assert.AreEqual(0, readerFactory.CreateCallCount);
    }

    [TestMethod]
    [DataRow("\\\\server\\share\\Worker.pdb")]
    [DataRow("M:\\Worker.pdb")]
    [DataRow("\\\\?\\C:\\app\\Worker.pdb")]
    [DataRow("\\\\.\\C:\\app\\Worker.pdb")]
    public async Task ResolveAsync_DefaultLookup_WhenMatchedPdbIsNonLocalPath_DoesNotReadSource(string matchedPdbPath)
    {
        const string localPdbPath = "C:\\app\\Worker.pdb";
        var fileSystem = new ControlledExecutionLocalFileSystem([localPdbPath], ["M:\\"]);
        var reader = new ControlledExecutionSymbolReader(matchedPdbPath, sourceLine: null);
        var symbolAddressInspector = new ControlledExecutionSymbolAddressInspector(
            new ExecutionModuleSymbolIdentity("Worker.pdb", Guid.NewGuid(), PdbAge: 1, FileVersion: null));
        await using var resolver = new ExecutionSymbolResolver(
            lookup: null,
            logger: null,
            fileSystem: fileSystem,
            symbolReaderFactory: new ControlledExecutionSymbolReaderFactory(reader),
            symbolAddressInspector: symbolAddressInspector);

        var location = await resolver.ResolveAsync(
            Frame(frameId: 45, modulePath: "C:\\app\\Worker.dll"),
            CancellationToken.None);

        Assert.IsNull(location);
        Assert.AreEqual(1, reader.FindSymbolCallCount);
        Assert.AreEqual(0, reader.SourceLineCallCount);
        Assert.HasCount(1, fileSystem.ExistenceProbes);
        Assert.AreEqual(Path.GetFullPath(localPdbPath), fileSystem.ExistenceProbes[0]);
    }

    [TestMethod]
    [DataRow("\\\\server\\share\\Worker.cs")]
    [DataRow("M:\\Worker.cs")]
    [DataRow("\\\\?\\C:\\app\\Worker.cs")]
    [DataRow("\\\\.\\C:\\app\\Worker.cs")]
    public async Task ResolveAsync_WhenSourceIsNonLocalPath_DoesNotProbeSourceExistence(string sourcePath)
    {
        var fileSystem = new ControlledExecutionLocalFileSystem([], ["M:\\"]);
        var lookup = new ControlledSourceLocationLookup(
            new ExecutionSourceLocationLookupResult(
                ExecutionSourceLocationLookupStatus.Found,
                sourcePath,
                LineNumber: 23,
                ColumnNumber: 4));
        await using var resolver = new ExecutionSymbolResolver(
            lookup: lookup,
            logger: null,
            fileSystem: fileSystem,
            symbolReaderFactory: null,
            symbolAddressInspector: null);

        var location = await resolver.ResolveAsync(Frame(frameId: 47), CancellationToken.None);

        Assert.IsNull(location);
        Assert.IsEmpty(fileSystem.ExistenceProbes);
    }

    private static ExecutionFrameReference Frame(int frameId) => new(
        frameId,
        new ExecutionFrameDescriptor("Worker.Run", "Worker", "C:\\Worker.dll", $"symbol-{frameId}"),
        SymbolAddress: null);

    private static ExecutionFrameReference Frame(
        int frameId,
        string modulePath) => new(
            frameId,
            new ExecutionFrameDescriptor("Worker.Run", "Worker", modulePath, $"symbol-{frameId}"),
            SymbolAddress: null);

    private static string CreateTemporarySourceFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"), "Worker.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "internal static class Worker { }");
        return path;
    }

    private static void DeleteTemporarySourceFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ControlledSourceLocationLookup : IExecutionSourceLocationLookup
    {
        private readonly ExecutionSourceLocationLookupResult? _result;
        private readonly Exception? _exception;
        private readonly bool _waitForRelease;
        private int _callCount;

        public ControlledSourceLocationLookup(
            ExecutionSourceLocationLookupResult result,
            bool waitForRelease = false)
        {
            _result = result;
            _waitForRelease = waitForRelease;
        }

        public ControlledSourceLocationLookup(Exception exception)
        {
            _exception = exception;
        }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public ExecutionSourceLocationLookupResult Lookup(ExecutionFrameReference frame)
        {
            _ = frame;
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            if (_waitForRelease)
            {
                Release.Task.GetAwaiter().GetResult();
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return _result!;
        }
    }

    private sealed class ThrowingLogger : ILogger<ExecutionSymbolResolver>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("Logger failed.");
    }

    private sealed class ControlledExecutionLocalFileSystem : IExecutionLocalFileSystem
    {
        private readonly HashSet<string> _existingFiles;
        private readonly HashSet<string> _networkRoots;

        public ControlledExecutionLocalFileSystem(
            IEnumerable<string> existingFiles,
            IEnumerable<string>? networkRoots = null)
        {
            _existingFiles = new HashSet<string>(
                existingFiles.Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            _networkRoots = new HashSet<string>(
                networkRoots?.Select(Path.GetFullPath) ?? [],
                StringComparer.OrdinalIgnoreCase);
        }

        public List<string> ExistenceProbes { get; } = [];

        public DriveType GetDriveType(string pathRoot) =>
            _networkRoots.Contains(Path.GetFullPath(pathRoot))
                ? DriveType.Network
                : DriveType.Fixed;

        public bool FileExists(string path)
        {
            var fullPath = Path.GetFullPath(path);
            ExistenceProbes.Add(fullPath);
            return _existingFiles.Contains(fullPath);
        }
    }

    private sealed class ControlledExecutionSymbolAddressInspector : IExecutionSymbolAddressInspector
    {
        private readonly ExecutionModuleSymbolIdentity _moduleIdentity;

        public ControlledExecutionSymbolAddressInspector(ExecutionModuleSymbolIdentity moduleIdentity)
        {
            _moduleIdentity = moduleIdentity;
        }

        public ExecutionModuleSymbolIdentity? GetModuleIdentity(ExecutionFrameReference frame)
        {
            _ = frame;
            return _moduleIdentity;
        }
    }

    private sealed class ControlledExecutionSymbolReaderFactory : IExecutionSymbolReaderFactory
    {
        private readonly IExecutionSymbolReader _reader;

        public ControlledExecutionSymbolReaderFactory(IExecutionSymbolReader reader)
        {
            _reader = reader;
        }

        public int CreateCallCount { get; private set; }

        public string? ModuleDirectory { get; private set; }

        public Func<string, bool>? SecurityCheck { get; private set; }

        public IExecutionSymbolReader Create(string moduleDirectory, Func<string, bool> securityCheck)
        {
            CreateCallCount++;
            ModuleDirectory = moduleDirectory;
            SecurityCheck = securityCheck;
            return _reader;
        }
    }

    private sealed class ControlledExecutionSymbolReader : IExecutionSymbolReader
    {
        private readonly string? _matchedPdbPath;
        private readonly ExecutionSourceLine? _sourceLine;

        public ControlledExecutionSymbolReader(string? matchedPdbPath, ExecutionSourceLine? sourceLine)
        {
            _matchedPdbPath = matchedPdbPath;
            _sourceLine = sourceLine;
        }

        public int FindSymbolCallCount { get; private set; }

        public int SourceLineCallCount { get; private set; }

        public string? PdbFileName { get; private set; }

        public ExecutionModuleSymbolIdentity? ModuleIdentity { get; private set; }

        public string? ModulePath { get; private set; }

        public string? FindSymbolFilePath(
            string pdbFileName,
            ExecutionModuleSymbolIdentity moduleIdentity,
            string modulePath)
        {
            FindSymbolCallCount++;
            PdbFileName = pdbFileName;
            ModuleIdentity = moduleIdentity;
            ModulePath = modulePath;
            return _matchedPdbPath;
        }

        public ExecutionSourceLine? GetSourceLine(ExecutionFrameReference frame)
        {
            _ = frame;
            SourceLineCallCount++;
            return _sourceLine;
        }

        public void Dispose()
        {
        }
    }
}
