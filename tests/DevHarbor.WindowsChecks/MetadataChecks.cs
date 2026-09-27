using System.Runtime.InteropServices;
using System.Security.AccessControl;
using DevHarbor.Windows;

internal static partial class Program
{
    private static void MetadataChecks()
    {
        Check("metadata-large-file-with-data-read-denied", () =>
        {
            var f = Fixture();
            const long length = 65L * 1024 * 1024;
            using (var stream = File.OpenWrite(f.File)) stream.SetLength(length);
            var file = new FileInfo(f.File);
            var original = file.GetAccessControl();
            var denied = file.GetAccessControl();
            denied.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
            file.SetAccessControl(denied);
            try
            {
                Reject(() => WindowsBoundary.Inspect(f.Root, f.File), BoundaryError.AccessDenied);
                var observed = WindowsBoundary.ReadMetadata(f.Root, f.File);
                Require(!observed.IsDirectory && observed.LogicalBytes == length && observed.AllocatedBytes >= 0, "Large metadata measurement failed");
                Require(observed.Identity == f.Snapshot.Stamp.Identity, "Metadata changed file identity");
            }
            finally { file.SetAccessControl(original); }
        });
        Check("metadata-sparse-file-over-four-gib", () =>
        {
            var f = Fixture();
            using (var handle = CreateFileW(f.File, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid || !DeviceIoControl(handle, 0x900C4, [], 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            const long length = 5L * 1024 * 1024 * 1024;
            using (var stream = File.OpenWrite(f.File)) stream.SetLength(length);
            var observed = WindowsBoundary.ReadMetadata(f.Root, f.File);
            Require(observed.LogicalBytes == length && observed.AllocatedBytes >= 0 && observed.AllocatedBytes < length, "Sparse sizes conflated or truncated");
        });
        Check("metadata-hardlinks-share-identity-without-allowing-mutation", () =>
        {
            var f = Fixture(); string alias = Path.Combine(f.Root, "alias.txt");
            if (!CreateHardLinkW(alias, f.File, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var first = WindowsBoundary.ReadMetadata(f.Root, f.File);
            var second = WindowsBoundary.ReadMetadata(f.Root, alias);
            Require(first.Identity == second.Identity && first.LinkCount == 2 && second.LinkCount == 2, "Shared identity missing");
            Require(first.LogicalBytes == second.LogicalBytes && first.AllocatedBytes == second.AllocatedBytes, "Shared sizes differ");
            Failure(HandleQuarantine.Stage(f.Snapshot, f.Store), BoundaryError.MultipleLinks);
        });
        Check("metadata-root-and-directory-are-not-zero-byte-totals", () =>
        {
            var f = Fixture(); string nested = Path.Combine(f.Root, "nested"); Directory.CreateDirectory(nested);
            foreach (string path in new[] { f.Root, nested })
            {
                var observed = WindowsBoundary.ReadMetadata(f.Root, path);
                Require(observed.IsDirectory && observed.LogicalBytes == null && observed.AllocatedBytes == null, "Directory reports a false total");
            }
            using (var stream = File.OpenWrite(f.File)) stream.SetLength(0);
            var empty = WindowsBoundary.ReadMetadata(f.Root, f.File);
            Require(!empty.IsDirectory && empty.LogicalBytes == 0 && empty.AllocatedBytes >= 0, "Empty file is not measured");
        });
        Check("metadata-boundary-and-invalid-paths-rejected", () =>
        {
            var f = Fixture();
            Reject(() => WindowsBoundary.ReadMetadata(f.Root, f.Store), BoundaryError.OutsideBoundary);
            Reject(() => WindowsBoundary.ReadMetadata(f.Root, f.Root + "-other\\file"), BoundaryError.OutsideBoundary);
            foreach (string bad in new[] { f.File + ":stream", f.File + ".", f.Root + "\\..\\outside", @"\\?\C:\file", @"\\server\share\file", "relative" })
                Reject(() => WindowsBoundary.ReadMetadata(f.Root, bad), BoundaryError.InvalidPath);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Reject(() => WindowsBoundary.ReadMetadata(profile, profile), BoundaryError.ProtectedPath);
        });
        Check("metadata-junction-and-offline-rejected", () =>
        {
            var f = Fixture(); string link = Path.Combine(f.Root, "junction"); Junction(link, f.Store);
            Reject(() => WindowsBoundary.ReadMetadata(f.Root, link), BoundaryError.ReparsePoint);
            Reject(() => WindowsBoundary.ReadMetadata(f.Root, Path.Combine(link, "missing")), BoundaryError.ReparsePoint);
            File.SetAttributes(f.File, File.GetAttributes(f.File) | FileAttributes.Offline);
            try { Reject(() => WindowsBoundary.ReadMetadata(f.Root, f.File), BoundaryError.UnsupportedFeature); }
            finally { File.SetAttributes(f.File, FileAttributes.Normal); }
        });
        Check("metadata-case-sensitive-directory-rejected", () =>
        {
            var f = Fixture(); string dir = Path.Combine(f.Root, "sensitive"); Directory.CreateDirectory(dir);
            SetCaseSensitive(dir, true);
            try { Reject(() => WindowsBoundary.ReadMetadata(f.Root, dir), BoundaryError.UnsupportedFeature); }
            finally { SetCaseSensitive(dir, false); }
        });
        Check("metadata-access-denied-and-missing-are-not-zero", () =>
        {
            var f = Fixture();
            Reject(() => WindowsBoundary.ReadMetadata(f.Root, Path.Combine(f.Root, "missing")), BoundaryError.TargetChanged);
            var directory = new DirectoryInfo(f.Root); var original = directory.GetAccessControl(); var denied = directory.GetAccessControl();
            denied.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            directory.SetAccessControl(denied);
            try { Reject(() => WindowsBoundary.ReadMetadata(f.Root, f.File), BoundaryError.AccessDenied); }
            finally { directory.SetAccessControl(original); }
        });
        Check("metadata-cancellation-and-handle-release", () =>
        {
            var f = Fixture(); using var cts = new CancellationTokenSource(); cts.Cancel();
            bool cancelled = false;
            try { WindowsBoundary.ReadMetadata(f.Root, f.File, cts.Token); }
            catch (OperationCanceledException e) when (e.CancellationToken == cts.Token) { cancelled = true; }
            Require(cancelled, "Cancellation ignored");
            WindowsBoundary.ReadMetadata(f.Root, f.File);
            File.Move(f.File, f.File + ".moved");
            Directory.Move(f.Root, f.Root + "-moved");
        });
        Check("metadata-unicode-long-path-and-observation-time", () =>
        {
            var f = Fixture(); string deep = Path.Combine(f.Root, "한글 공백", new string('a', 100), new string('b', 100)); Directory.CreateDirectory(deep);
            string file = Path.Combine(deep, "파일.txt"); File.WriteAllText(file, "fixture");
            var before = DateTimeOffset.UtcNow;
            var observed = WindowsBoundary.ReadMetadata(f.Root, file);
            Require(observed.Path == file && observed.Root == f.Root && observed.LogicalBytes == 7, "Long path metadata mismatch");
            Require(observed.ObservedAt >= before && observed.ObservedAt <= DateTimeOffset.UtcNow, "Invalid observation time");
        });
    }
}
