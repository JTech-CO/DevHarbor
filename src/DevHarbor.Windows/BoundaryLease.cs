using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DevHarbor.Windows;

// Read-only public inspection. Mutation stays internal to the P1 integration harness.
public static class WindowsBoundary
{
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

    private static BoundaryLease Open(string root, string? file, bool mutation, CancellationToken token)
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
                bool leaf = file != null && i == components.Length - 1;
                var handle = NativeFile.Open(current, !leaf, mutation);
                lease.handles.Add(handle);
                var info = NativeFile.Info(handle);
                if ((info.Attributes & NativeFile.Reparse) != 0) throw new BoundaryException(BoundaryError.ReparsePoint, "Reparse components are not traversed");
                if ((info.Attributes & NativeFile.OfflineOrRecall) != 0) throw new BoundaryException(BoundaryError.UnsupportedFeature, "Offline or recall content is not read");
                if (!string.Equals(NativeFile.FinalPath(handle), current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new BoundaryException(BoundaryError.InvalidPath, "Aliases and substituted drives are not accepted");
                if (i == -1) NativeFile.RequireNtfs(handle);
                if (leaf)
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
