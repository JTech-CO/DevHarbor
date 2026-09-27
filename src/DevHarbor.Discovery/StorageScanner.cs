using System.Diagnostics;
using DevHarbor.Windows;

namespace DevHarbor.Discovery;

public sealed class StorageScanner(IStorageFileSystem? fileSystem = null, ScanBudget? scanBudget = null)
{
    private readonly IStorageFileSystem fs = fileSystem ?? new WindowsStorageFileSystem();
    private readonly ScanBudget budget = scanBudget ?? new();
    private sealed record Key(string Drive, FileIdentity Identity);
    private sealed class Observed(EntryMetadata data)
    {
        public EntryMetadata Data { get; } = data;
        public HashSet<int> Owners { get; } = [];
    }
    private sealed class Row(StoreDescriptor store)
    {
        public StoreDescriptor Store { get; } = store;
        public HashSet<Key> Files { get; } = [];
        public List<ScanIssue> Issues { get; } = [];
        public int Skipped;
        public bool Measured;
        public ScanStatus Status = ScanStatus.Unmeasured;
    }

    public Task<ScanReport> ScanAsync(IReadOnlyList<StoreDescriptor> stores, IProgress<ScanProgress>? progress = null, CancellationToken token = default)
    {
        if (stores.Count > 100 || budget.MaxEntries < 1 || budget.MaxDepth < 1 || budget.MaxIssues < 1 || budget.TimeLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stores), "Invalid scan budget or too many stores");
        return Task.Run(() => Scan(stores, progress, token));
    }

    private ScanReport Scan(IReadOnlyList<StoreDescriptor> stores, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); var reportClock = Stopwatch.StartNew();
        var global = new Dictionary<Key, Observed>(); var rows = stores.Select(s => new Row(s)).ToArray();
        int entries = 0;
        void Issue(Row row, string path, string reason)
        {
            row.Skipped++; row.Status = ScanStatus.Partial;
            if (row.Issues.Count < budget.MaxIssues) row.Issues.Add(new(path, reason));
        }
        void CheckBudget()
        {
            token.ThrowIfCancellationRequested();
            if (entries >= budget.MaxEntries || clock.Elapsed >= budget.TimeLimit) throw new CommandFailure("ScanBudget");
        }
        for (int index = 0; index < rows.Length; index++)
        {
            var row = rows[index]; var store = row.Store;
            if (store.Status != DiscoveryStatus.Ready) continue;
            if (store.Environment == StorageEnvironment.LocalEngine)
            {
                row.Status = store.EngineUsage == null ? ScanStatus.Unmeasured : ScanStatus.EngineReported;
                continue;
            }
            if (store.Environment != StorageEnvironment.NativeLocal || store.Root == null) continue;
            try
            {
                CheckBudget();
                var root = fs.Root(store.Root, token);
                if (!root.IsDirectory) throw new BoundaryException(BoundaryError.InvalidPath, "Storage root must be a directory");
                var stack = new Stack<(string Path, FileIdentity Identity, int Depth)>();
                stack.Push((root.Path, root.Identity, 0)); row.Status = ScanStatus.Complete;
                while (stack.TryPop(out var directory))
                {
                    CheckBudget();
                    try
                    {
                        foreach (var child in fs.Children(root.Path, directory.Path, root.Identity, directory.Identity, token))
                        {
                            CheckBudget(); entries++;
                            string path = Path.Combine(directory.Path, child.Name);
                            try
                            {
                                var metadata = fs.Read(root.Path, path, root.Identity, child.Identity, token);
                                if (metadata.IsDirectory)
                                {
                                    if (directory.Depth >= budget.MaxDepth) Issue(row, path, "DepthBudget");
                                    else stack.Push((path, metadata.Identity, directory.Depth + 1));
                                }
                                else
                                {
                                    var key = new Key(Path.GetPathRoot(root.Path)!.ToUpperInvariant(), metadata.Identity);
                                    if (!global.TryGetValue(key, out var observed)) global.Add(key, observed = new(metadata));
                                    else if (observed.Data.LogicalBytes != metadata.LogicalBytes || observed.Data.AllocatedBytes != metadata.AllocatedBytes
                                        || observed.Data.LastWrite != metadata.LastWrite) Issue(row, path, "TargetChanged");
                                    observed.Owners.Add(index); row.Files.Add(key); row.Measured = true;
                                }
                            }
                            catch (Exception e) when (IsItemError(e)) { Issue(row, path, Reason(e)); }
                            if (reportClock.ElapsedMilliseconds >= 100)
                            {
                                progress?.Report(new(store.Id, entries, index)); reportClock.Restart();
                            }
                        }
                        // Only successful enumeration can establish a measured empty directory.
                        if (row.Skipped == 0) row.Measured = true;
                    }
                    catch (Exception e) when (IsItemError(e)) { Issue(row, directory.Path, Reason(e)); }
                }
            }
            catch (OperationCanceledException) { row.Status = ScanStatus.Cancelled; }
            catch (CommandFailure e) { Issue(row, store.Root, e.Reason); }
            catch (Exception e) when (IsItemError(e)) { Issue(row, store.Root, Reason(e)); }
            progress?.Report(new(store.Id, entries, index + 1));
        }
        var measurements = rows.Select(row => new StoreMeasurement(row.Store, row.Status,
            row.Measured ? row.Files.Sum(k => global[k].Data.LogicalBytes!.Value) : null,
            row.Measured ? row.Files.Sum(k => global[k].Data.AllocatedBytes!.Value) : null,
            row.Measured ? row.Files.Where(k => global[k].Owners.Count > 1 || global[k].Data.LinkCount > 1).Sum(k => global[k].Data.LogicalBytes!.Value) : null,
            row.Files.Count, row.Skipped, row.Issues.AsReadOnly(), DateTimeOffset.UtcNow)).ToArray();
        bool any = rows.Any(r => r.Measured);
        return new(measurements, any ? global.Values.Sum(v => v.Data.LogicalBytes!.Value) : null,
            any ? global.Values.Sum(v => v.Data.AllocatedBytes!.Value) : null,
            rows.Count(r => !r.Measured), rows.Any(r => r.Status is ScanStatus.Partial or ScanStatus.Cancelled), DateTimeOffset.UtcNow);
    }
    private static bool IsItemError(Exception e) => e is not CommandFailure && (e is BoundaryException or UnauthorizedAccessException or IOException or ArgumentException);
    private static string Reason(Exception e) => e is BoundaryException boundary ? boundary.Reason.ToString()
        : e is UnauthorizedAccessException ? "AccessDenied" : e is ArgumentException ? "InvalidPath" : "IoFailure";
}
