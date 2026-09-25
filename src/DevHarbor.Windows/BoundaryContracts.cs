namespace DevHarbor.Windows;

public enum BoundaryError
{
    InvalidPath, OutsideBoundary, ProtectedPath, UnsupportedPlatform, UnsupportedFileSystem,
    UnsupportedFeature, ReparsePoint, MultipleLinks, Busy, AccessDenied, TargetChanged,
    DestinationExists, Cancelled, IoFailure
}

public sealed class BoundaryException(BoundaryError reason, string message, int? nativeError = null)
    : IOException(message)
{
    public BoundaryError Reason { get; } = reason;
    public int? NativeError { get; } = nativeError;
}

public sealed record FileIdentity(uint Volume, ulong FileId);
public sealed record FileStamp(FileIdentity Identity, long Length, long LastWrite, string Sha256);
public sealed record FileSnapshot(string Root, string Path, string AncestorIdentity, FileStamp Stamp);

// No production caller can enable mutation until P3 provides approval + durable ledger.
public static class WindowsCapabilities
{
    public static bool CanRecycle => false;
    public static bool CanPermanentlyDelete => false;
    public static string RecycleUnavailableReason => "No verified race-free Shell handoff; preserve the original or quarantined file.";
}

internal sealed record QuarantineReceipt(FileSnapshot Original, string Store, string StoreIdentity, string StoredName)
{
    public string StoredPath => System.IO.Path.Combine(Store, StoredName);
}

internal sealed record MoveOutcome(bool Completed, BoundaryError? Error = null,
    QuarantineReceipt? Receipt = null, string? RestoredPath = null, int? NativeError = null);
