using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevHarbor.Windows;

internal static class NativeFile
{
    internal const uint ReadAttributes = 0x80, GenericRead = 0x80000000, Delete = 0x10000;
    internal const uint Directory = 0x10, Reparse = 0x400, OfflineOrRecall = 0x1000 | 0x40000 | 0x400000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInfo
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        internal readonly FileIdentity Identity => new(Volume, ((ulong)IndexHigh << 32) | IndexLow);
        internal readonly long Length => checked((long)(((ulong)SizeHigh << 32) | SizeLow));
        internal readonly long LastWrite => ((long)Written.dwHighDateTime << 32) | (uint)Written.dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RenameInfo
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out uint flags, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volumeName, uint volumeLength, out uint serial, out uint maximumComponent, out uint flags, StringBuilder fileSystem, uint fileSystemLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock ioStatus, IntPtr info, uint length, int kind);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    internal static BoundaryException Error(string operation, int? nativeCode = null)
    {
        int code = nativeCode ?? Marshal.GetLastWin32Error();
        var reason = code switch
        {
            5 => BoundaryError.AccessDenied,
            32 or 33 => BoundaryError.Busy,
            80 or 183 => BoundaryError.DestinationExists,
            2 or 3 => BoundaryError.TargetChanged,
            _ => BoundaryError.IoFailure
        };
        return new(reason, $"{operation}: {new Win32Exception(code).Message}", code);
    }

    internal static SafeFileHandle Open(string path, bool directory, bool mutation)
    {
        // Attribute-only directory handles do not reliably prevent an empty directory rename.
        // FILE_LIST_DIRECTORY participates in sharing checks and pins the destination too.
        uint access = directory ? ReadAttributes | 1u : GenericRead | (mutation ? Delete : 0);
        // Never grant delete sharing. Ancestors cannot be renamed/replaced during traversal.
        var handle = CreateFileW(@"\\?\" + path, access, directory ? 3u : 1u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Error("Open boundary component");
        handle.Dispose();
        throw error;
    }

    internal static FileInfo Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Error("Read identity");
        return info;
    }

    internal static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw Error("Resolve handle path");
        string path = buffer.ToString();
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new BoundaryException(BoundaryError.InvalidPath, "Unexpected final path namespace");
        return path[4..].TrimEnd('\\');
    }

    internal static void RequireNtfs(SafeFileHandle handle)
    {
        var fs = new StringBuilder(64);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, fs, (uint)fs.Capacity)) throw Error("Query volume");
        if (fs.ToString() != "NTFS") throw new BoundaryException(BoundaryError.UnsupportedFileSystem, "Only NTFS is supported");
    }

    internal static void RequireInsensitiveDirectory(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 23, out uint flags, 4))
            throw new BoundaryException(BoundaryError.UnsupportedFeature, "Cannot verify directory case-sensitivity", Marshal.GetLastWin32Error());
        if ((flags & 1) != 0) throw new BoundaryException(BoundaryError.UnsupportedFeature, "Case-sensitive directories are not supported");
    }

    internal static void Rename(SafeFileHandle file, SafeFileHandle destinationDirectory, string leaf)
    {
        BoundaryPath.ValidateLeaf(leaf);
        // Relative native rename uses the pinned directory object, never re-resolves its path.
        byte[] name = Encoding.Unicode.GetBytes(leaf);
        int offset = Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileName)).ToInt32();
        // Win32 path conversion also reads the terminating WCHAR; FileNameLength excludes it.
        int size = Math.Max(Marshal.SizeOf<RenameInfo>(), offset + name.Length + sizeof(char));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        bool reference = false;
        try
        {
            destinationDirectory.DangerousAddRef(ref reference);
            Marshal.Copy(new byte[size], 0, buffer, size);
            // ReplaceIfExists=false: collision detection and no-overwrite are atomic in NTFS.
            var info = new RenameInfo { RootDirectory = destinationDirectory.DangerousGetHandle(), FileNameLength = (uint)name.Length };
            Marshal.StructureToPtr(info, buffer, false);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, offset), name.Length);
            int status = NtSetInformationFile(file, out _, buffer, (uint)size, 10);
            if (status != 0) throw Error("Rename exact file handle", (int)RtlNtStatusToDosError(status));
        }
        finally
        {
            if (reference) destinationDirectory.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }
}
