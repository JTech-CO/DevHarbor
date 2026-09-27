using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DevHarbor.Windows;

// Read-only public inspection. Mutation stays internal to the managed workflow and integration harness.
public static partial class WindowsBoundary
{
    // Does not enumerate descendants or read file data; safe for large cache observations.
    public static EntryMetadata ReadMetadata(string root, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = BoundaryLease.Metadata(root, path, cancellationToken);
        return lease.Observation!;
    }

    public static FileSnapshot Inspect(string root, string path, CancellationToken cancellationToken = default)
    {
        using var lease = BoundaryLease.File(root, path, false, cancellationToken);
        return lease.Snapshot!;
    }
}

internal sealed class BoundaryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal SafeFileHandle Handle => handles[^1];
    internal SafeFileHandle ParentHandle => handles[^2];
    internal string DirectoryIdentity { get; private set; } = "";
    internal FileIdentity? ScopeIdentity { get; private set; }
    internal EntryMetadata? Observation { get; private set; }
    internal FileSnapshot? Snapshot { get; private set; }

    internal static BoundaryLease Directory(string path, CancellationToken cancellationToken)
        => Open(BoundaryPath.Root(path), null, false, cancellationToken);

    internal static BoundaryLease File(string root, string path, bool mutation, CancellationToken cancellationToken)
    {
        root = BoundaryPath.Root(root);
        path = BoundaryPath.Canonical(path);
        if (!BoundaryPath.Contains(root, path)) throw new BoundaryException(BoundaryError.OutsideBoundary, "File must be below the approved scope");
        return Open(root, path, mutation, cancellationToken);
    }

    internal static BoundaryLease Metadata(string root, string path, CancellationToken token)
    {
        root = BoundaryPath.Root(root);
        path = BoundaryPath.Canonical(path);
        if (!string.Equals(root, path, StringComparison.OrdinalIgnoreCase) && !BoundaryPath.Contains(root, path))
            throw new BoundaryException(BoundaryError.OutsideBoundary, "Entry must be within the scan scope");
        return Open(root, path, false, token, metadataOnly: true);
    }

    internal static BoundaryLease ScanDirectory(string root, string path, FileIdentity scope, FileIdentity directory, CancellationToken token)
    {
        root = BoundaryPath.Root(root); path = BoundaryPath.Canonical(path);
        if (!string.Equals(root, path, StringComparison.OrdinalIgnoreCase) && !BoundaryPath.Contains(root, path))
            throw new BoundaryException(BoundaryError.OutsideBoundary, "Directory is outside scope");
        var lease = Open(root, path, false, token, directoryTarget: true);
        try
        {
            if (lease.ScopeIdentity == scope && NativeFile.Info(lease.Handle).Identity == directory) return lease;
            throw new BoundaryException(BoundaryError.TargetChanged, "Scan directory was replaced");
        }
        catch { lease.Dispose(); throw; }
    }

    private static BoundaryLease Open(string root, string? file, bool mutation, CancellationToken token, bool metadataOnly = false, bool directoryTarget = false)
    {
        var lease = new BoundaryLease();
        try
        {
            string target = file ?? root;
            string current = Path.GetPathRoot(target)!;
            string[] components = target[3..].Split('\\');
            var identities = new List<string>();
            for (int i = -1; i < components.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i >= 0) current = Path.Combine(current, components[i]);
                bool leaf = file != null && i == components.Length - 1 && !directoryTarget;
                var handle = NativeFile.Open(current, !leaf, mutation, metadataOnly && leaf);
                lease.handles.Add(handle);
                var info = NativeFile.Info(handle);
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) lease.ScopeIdentity = info.Identity;
                if ((info.Attributes & NativeFile.Reparse) != 0) throw new BoundaryException(BoundaryError.ReparsePoint, "Reparse components are not traversed");
                if ((info.Attributes & NativeFile.OfflineOrRecall) != 0) throw new BoundaryException(BoundaryError.UnsupportedFeature, "Offline or recall content is not read");
                if (!string.Equals(NativeFile.FinalPath(handle), current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new BoundaryException(BoundaryError.InvalidPath, "Aliases and substituted drives are not accepted");
                if (i == -1) NativeFile.RequireNtfs(handle);
                if (leaf && metadataOnly)
                {
                    bool directory = (info.Attributes & NativeFile.Directory) != 0;
                    if (directory) NativeFile.RequireInsensitiveDirectory(handle);
                    var sizes = NativeFile.Standard(handle);
                    var after = NativeFile.Info(handle);
                    if (after.Identity != info.Identity || after.Attributes != info.Attributes || after.Length != info.Length ||
                        after.LastWrite != info.LastWrite || after.Links != info.Links ||
                        sizes.EndOfFile != info.Length || sizes.NumberOfLinks != info.Links || (sizes.Directory != 0) != directory)
                        throw new BoundaryException(BoundaryError.TargetChanged, "Entry changed during metadata inspection");
                    token.ThrowIfCancellationRequested();
                    lease.Observation = new(root, file!, info.Identity, directory,
                        directory ? null : sizes.EndOfFile, directory ? null : sizes.AllocationSize,
                        info.Links, info.LastWrite, DateTimeOffset.UtcNow);
                }
                else if (leaf)
                {
                    if ((info.Attributes & NativeFile.Directory) != 0) throw new BoundaryException(BoundaryError.UnsupportedFeature, "Recursive directory mutations are not supported");
                    if (info.Links != 1) throw new BoundaryException(BoundaryError.MultipleLinks, "Shared hardlinks are not mutation candidates");
                    // Bounded P1 surface: content identity is verified without unbounded model hashing.
                    if (info.Length > 64 * 1024 * 1024) throw new BoundaryException(BoundaryError.UnsupportedFeature, "P1 single-file limit is 64 MiB");
                    string digest = Hash(handle, info.Length, token);
                    var after = NativeFile.Info(handle);
                    if (after.Identity != info.Identity || after.Length != info.Length || after.LastWrite != info.LastWrite || after.Links != 1)
                        throw new BoundaryException(BoundaryError.TargetChanged, "File changed during inspection");
                    lease.Snapshot = new(root, file!, string.Join("/", identities), new(info.Identity, info.Length, info.LastWrite, digest));
                }
                else
                {
                    if ((info.Attributes & NativeFile.Directory) == 0) throw new BoundaryException(BoundaryError.InvalidPath, "Ancestor is not a directory");
                    NativeFile.RequireInsensitiveDirectory(handle);
                    identities.Add($"{info.Volume:X8}:{info.Identity.FileId:X16}");
                }
            }
            lease.DirectoryIdentity = string.Join("/", identities);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static string Hash(SafeFileHandle file, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            token.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
            if (read == 0) throw new BoundaryException(BoundaryError.TargetChanged, "Unexpected end of file");
            hash.AppendData(buffer.AsSpan(0, read));
            offset += read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        handles.Clear();
    }
}
