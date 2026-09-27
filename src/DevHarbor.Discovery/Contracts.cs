using DevHarbor.Windows;

namespace DevHarbor.Discovery;

public enum ToolKind { Pip, Uv, Npm, Ollama, HuggingFace, Docker, Custom }
public enum StorageEnvironment { NativeLocal, LocalEngine, Remote }
public enum DiscoveryStatus { Ready, NotInstalled, Unavailable, InvalidConfiguration, RemoteExcluded }
public enum ScanStatus { Complete, Partial, Cancelled, Unmeasured, EngineReported }
public sealed record StoreDescriptor(string Id, ToolKind Tool, string? Root, string Version,
    string Evidence, DiscoveryStatus Status = DiscoveryStatus.Ready,
    StorageEnvironment Environment = StorageEnvironment.NativeLocal, string? EngineUsage = null);
public sealed record ScanIssue(string Path, string Reason);
public sealed record StoreMeasurement(StoreDescriptor Store, ScanStatus Status, long? LogicalBytes,
    long? AllocatedBytes, long? SharedLogicalBytes, int UniqueFiles, int SkippedEntries,
    IReadOnlyList<ScanIssue> Issues, DateTimeOffset ObservedAt);
public sealed record ScanReport(IReadOnlyList<StoreMeasurement> Stores, long? UniqueLogicalBytes,
    long? UniqueAllocatedBytes, int UnmeasuredStores, bool IsPartial, DateTimeOffset CompletedAt);
public sealed record ScanProgress(string StoreId, int Entries, int CompletedStores);
public sealed record ScanBudget(int MaxEntries = 100000, int MaxDepth = 128, int MaxIssues = 100, TimeSpan? Duration = null)
{
    public TimeSpan TimeLimit => Duration ?? TimeSpan.FromSeconds(60);
}

public interface IStorageFileSystem
{
    EntryMetadata Root(string root, CancellationToken token);
    EntryMetadata Read(string root, string path, FileIdentity scope, FileIdentity expected, CancellationToken token);
    IEnumerable<DirectoryEntry> Children(string root, string path, FileIdentity scope, FileIdentity expected, CancellationToken token);
}
public sealed class WindowsStorageFileSystem : IStorageFileSystem
{
    public EntryMetadata Root(string root, CancellationToken token) => WindowsBoundary.ReadMetadata(root, root, token);
    public EntryMetadata Read(string root, string path, FileIdentity scope, FileIdentity expected, CancellationToken token)
        => WindowsBoundary.ReadScanMetadata(root, path, scope, expected, token);
    public IEnumerable<DirectoryEntry> Children(string root, string path, FileIdentity scope, FileIdentity expected, CancellationToken token)
        => WindowsBoundary.ReadDirectory(root, path, scope, expected, token);
}
