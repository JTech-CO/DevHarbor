using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevHarbor.Windows;

public sealed record DirectoryEntry(string Name, FileIdentity Identity, uint Attributes);

public static partial class WindowsBoundary
{
    public static EntryMetadata ReadScanMetadata(string root, string path, FileIdentity scope, FileIdentity expected, CancellationToken token = default)
    {
        using var lease = BoundaryLease.Metadata(root, path, token);
        if (lease.ScopeIdentity != scope || lease.Observation!.Identity != expected)
            throw new BoundaryException(BoundaryError.TargetChanged, "Scan item was replaced");
        return lease.Observation;
    }

    // Enumeration uses the pinned directory object, never a second path-based enumeration.
    public static IEnumerable<DirectoryEntry> ReadDirectory(string root, string path, FileIdentity scope, FileIdentity expected, CancellationToken token = default)
    {
        using var lease = BoundaryLease.ScanDirectory(root, path, scope, expected, token);
        const int capacity = 65536, header = 104; // FILE_ID_BOTH_DIR_INFO, Windows x64
        IntPtr buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            bool first = true;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!GetFileInformationByHandleEx(lease.Handle, first ? 11 : 10, buffer, capacity))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 18) yield break; // ERROR_NO_MORE_FILES
                    throw NativeFile.Error("Enumerate directory handle", error);
                }
                first = false;
                int offset = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (offset < 0 || offset > capacity - header) throw InvalidBuffer();
                    IntPtr entry = IntPtr.Add(buffer, offset);
                    uint next = unchecked((uint)Marshal.ReadInt32(entry));
                    uint bytes = unchecked((uint)Marshal.ReadInt32(entry, 60));
                    if (bytes == 0 || (bytes & 1) != 0 || bytes > capacity - offset - header ||
                        (next != 0 && (next < header + bytes || next > capacity - offset))) throw InvalidBuffer();
                    string name = Marshal.PtrToStringUni(IntPtr.Add(entry, header), (int)bytes / 2)!;
                    if (name is not ("." or ".."))
                    {
                        BoundaryPath.ValidateLeaf(name);
                        yield return new(name, new(scope.Volume, unchecked((ulong)Marshal.ReadInt64(entry, 96))), unchecked((uint)Marshal.ReadInt32(entry, 56)));
                    }
                    if (next == 0) break;
                    offset += (int)next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static BoundaryException InvalidBuffer() => new(BoundaryError.IoFailure, "Invalid directory information buffer");
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, IntPtr info, uint length);
}
