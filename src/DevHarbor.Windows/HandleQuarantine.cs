namespace DevHarbor.Windows;

// Experimental primitive only. P3 must add real approval and a write-ahead ledger.
// There are no deletion, Shell fallback, cross-volume copy, or overwrite APIs here.
internal static class HandleQuarantine
{
    internal static MoveOutcome Stage(FileSnapshot expected, string store, CancellationToken token = default, Action? beforeCommit = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            store = BoundaryPath.Root(store);
            if (store.Equals(expected.Root, StringComparison.OrdinalIgnoreCase) || BoundaryPath.Contains(expected.Root, store) || BoundaryPath.Contains(store, expected.Root))
                throw new BoundaryException(BoundaryError.OutsideBoundary, "Source and quarantine scopes must not overlap");
            using var source = BoundaryLease.File(expected.Root, expected.Path, true, token);
            if (source.Snapshot != expected) throw new BoundaryException(BoundaryError.TargetChanged, "Approval snapshot no longer matches");
            using var destination = BoundaryLease.Directory(store, token);
            if (NativeFile.Info(source.Handle).Volume != NativeFile.Info(destination.Handle).Volume)
                throw new BoundaryException(BoundaryError.UnsupportedFileSystem, "Cross-volume rename is not supported");
            var receipt = new QuarantineReceipt(expected, store, destination.DirectoryIdentity, Guid.NewGuid().ToString("N") + ".quarantine");
            beforeCommit?.Invoke(); // Internal deterministic race injection, not exposed to product callers.
            token.ThrowIfCancellationRequested();
            NativeFile.Rename(source.Handle, destination.Handle, receipt.StoredName);
            return new(true, Receipt: receipt); // Rename is the commit point. Cancellation after it cannot mean 'no change'.
        }
        catch (OperationCanceledException) { return new(false, BoundaryError.Cancelled); }
        catch (BoundaryException e) { return new(false, e.Reason, NativeError: e.NativeError); }
    }

    internal static MoveOutcome Restore(QuarantineReceipt receipt, string? alternateLeaf = null, CancellationToken token = default, Action? beforeCommit = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            BoundaryPath.ValidateLeaf(receipt.StoredName);
            string leaf = alternateLeaf ?? Path.GetFileName(receipt.Original.Path);
            BoundaryPath.ValidateLeaf(leaf);
            using var source = BoundaryLease.File(receipt.Store, receipt.StoredPath, true, token);
            if (source.Snapshot!.Stamp != receipt.Original.Stamp || source.DirectoryIdentity != receipt.StoreIdentity)
                throw new BoundaryException(BoundaryError.TargetChanged, "Quarantine identity changed");
            string parent = Path.GetDirectoryName(receipt.Original.Path)!;
            using var destination = BoundaryLease.Directory(parent, token);
            if (destination.DirectoryIdentity != receipt.Original.AncestorIdentity)
                throw new BoundaryException(BoundaryError.TargetChanged, "Original ancestor identity changed");
            string restored = Path.Combine(parent, leaf);
            beforeCommit?.Invoke();
            token.ThrowIfCancellationRequested();
            NativeFile.Rename(source.Handle, destination.Handle, leaf);
            return new(true, Receipt: receipt, RestoredPath: restored);
        }
        catch (OperationCanceledException) { return new(false, BoundaryError.Cancelled); }
        catch (BoundaryException e) { return new(false, e.Reason, NativeError: e.NativeError); }
    }

    internal static MoveOutcome RequestRecycle(QuarantineReceipt receipt)
        => new(false, BoundaryError.UnsupportedFeature, Receipt: receipt);
}
