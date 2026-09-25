using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevHarbor.Windows;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static int failures;
    private static string run = "";

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--restore") return RestoreWorker(args[1]);
        run = Path.GetFullPath(Path.Combine("artifacts", "p1", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(run);
        Check("positive-control-and-fresh-process-restore", () =>
        {
            var f = Fixture();
            File.WriteAllText(f.File + ":fixture-metadata", "alternate stream retained");
            var expected = WindowsBoundary.Inspect(f.Root, f.File);
            string hash = Digest(f.File);
            string acl = new FileInfo(f.File).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var result = HandleQuarantine.Stage(expected, f.Store);
            Success(result);
            Require(!File.Exists(f.File) && File.Exists(result.Receipt!.StoredPath), "File did not move");
            Require(Digest(result.Receipt!.StoredPath) == hash, "Staged bytes changed");
            Require(new FileInfo(result.Receipt.StoredPath).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) == acl, "ACL changed");
            string manifest = Path.Combine(f.Base, "receipt.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(result.Receipt));
            var psi = Worker(); psi.ArgumentList.Add("--restore"); psi.ArgumentList.Add(manifest);
            Execute(psi);
            Require(File.Exists(f.File) && Digest(f.File) == hash && !File.Exists(result.Receipt.StoredPath), "Fresh process restore failed");
            Require(File.ReadAllText(f.File + ":fixture-metadata") == "alternate stream retained", "Alternate stream was not preserved");
        });
        Check("unicode-spaces-and-long-path", () =>
        {
            var f = Fixture();
            string deep = Path.Combine(f.Root, "한글 공백", new string('a', 100), new string('b', 100));
            Directory.CreateDirectory(deep);
            string file = Path.Combine(deep, "파일.txt"); File.WriteAllText(file, "fixture");
            var result = HandleQuarantine.Stage(WindowsBoundary.Inspect(f.Root, file), f.Store);
            Success(result); Success(HandleQuarantine.Restore(result.Receipt!));
            Require(File.ReadAllText(file) == "fixture", "Long-path bytes changed");
        });
        Check("sibling-prefix-root-and-traversal-rejected", () =>
        {
            var f = Fixture();
            string sibling = f.Root + "-other"; Directory.CreateDirectory(sibling);
            string other = Path.Combine(sibling, "outside.txt"); File.WriteAllText(other, "sentinel");
            Reject(() => WindowsBoundary.Inspect(f.Root, other), BoundaryError.OutsideBoundary);
            Reject(() => WindowsBoundary.Inspect(f.Root, f.Root), BoundaryError.OutsideBoundary);
            Reject(() => WindowsBoundary.Inspect(f.Root, f.Root + "\\..\\other"), BoundaryError.InvalidPath);
            Require(File.ReadAllText(other) == "sentinel", "Outside file changed");
        });
        Check("device-unc-stream-relative-ambiguous-paths-rejected", () =>
        {
            var f = Fixture();
            foreach (string bad in new[] { @"\\localhost\C$\file", @"\\?\C:\file", "relative.txt", f.File + ":secret", f.File + ".", f.File + " ", Path.Combine(f.Root, "NUL.txt") })
                Reject(() => WindowsBoundary.Inspect(f.Root, bad), BoundaryError.InvalidPath);
        });
        Check("system-and-profile-roots-rejected", () =>
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Reject(() => WindowsBoundary.Inspect(system, Path.Combine(system, "test")), BoundaryError.ProtectedPath);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Reject(() => WindowsBoundary.Inspect(profile, Path.Combine(profile, "test")), BoundaryError.ProtectedPath);
        });
        Check("junction-escape-and-root-alias-rejected", () =>
        {
            var f = Fixture();
            string outside = Path.Combine(f.Base, "outside"); Directory.CreateDirectory(outside);
            string sentinel = Path.Combine(outside, "sentinel.txt"); File.WriteAllText(sentinel, "untouched");
            string link = Path.Combine(f.Root, "junction"); Junction(link, outside);
            Reject(() => WindowsBoundary.Inspect(f.Root, Path.Combine(link, "sentinel.txt")), BoundaryError.ReparsePoint);
            Reject(() => WindowsBoundary.Inspect(link, Path.Combine(link, "sentinel.txt")), BoundaryError.ReparsePoint);
            Require(File.ReadAllText(sentinel) == "untouched", "Sentinel changed");
        });
        Check("hardlink-alias-rejected", () =>
        {
            var f = Fixture();
            if (!CreateHardLinkW(Path.Combine(f.Root, "alias.txt"), f.File, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Reject(() => WindowsBoundary.Inspect(f.Root, f.File), BoundaryError.MultipleLinks);
        });
        Check("directory-and-offline-content-rejected", () =>
        {
            var f = Fixture();
            string dir = Path.Combine(f.Root, "nested"); Directory.CreateDirectory(dir);
            Reject(() => WindowsBoundary.Inspect(f.Root, dir), BoundaryError.UnsupportedFeature);
            File.SetAttributes(f.File, File.GetAttributes(f.File) | FileAttributes.Offline);
            try { Reject(() => WindowsBoundary.Inspect(f.Root, f.File), BoundaryError.UnsupportedFeature); }
            finally { File.SetAttributes(f.File, FileAttributes.Normal); }
        });
        Check("case-sensitive-directory-rejected", () =>
        {
            var f = Fixture();
            string dir = Path.Combine(f.Root, "sensitive"); Directory.CreateDirectory(dir);
            SetCaseSensitive(dir, true);
            try
            {
                string file = Path.Combine(dir, "item.txt"); File.WriteAllText(file, "fixture");
                Reject(() => WindowsBoundary.Inspect(f.Root, file), BoundaryError.UnsupportedFeature);
            }
            finally { SetCaseSensitive(dir, false); }
        });
        Check("exclusive-lock-rejected", () =>
        {
            var f = Fixture(); using var held = File.Open(f.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Failure(HandleQuarantine.Stage(f.Snapshot, f.Store), BoundaryError.Busy);
        });
        Check("access-denied-is-distinct-and-does-not-move-file", () =>
        {
            var f = Fixture();
            var file = new FileInfo(f.File);
            var original = file.GetAccessControl();
            var denied = file.GetAccessControl();
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            denied.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadData, AccessControlType.Deny));
            file.SetAccessControl(denied);
            try { Failure(HandleQuarantine.Stage(f.Snapshot, f.Store), BoundaryError.AccessDenied); }
            finally { file.SetAccessControl(original); }
            Require(File.Exists(f.File) && !Directory.EnumerateFiles(f.Store).Any(), "Denied operation moved a file");
        });
        Check("bounded-size-and-empty-file", () =>
        {
            var f = Fixture();
            using (var stream = File.OpenWrite(f.File)) stream.SetLength(64L * 1024 * 1024 + 1);
            Reject(() => WindowsBoundary.Inspect(f.Root, f.File), BoundaryError.UnsupportedFeature);
            using (var stream = File.OpenWrite(f.File)) stream.SetLength(0);
            var stage = HandleQuarantine.Stage(WindowsBoundary.Inspect(f.Root, f.File), f.Store);
            Success(stage); Success(HandleQuarantine.Restore(stage.Receipt!));
            Require(new FileInfo(f.File).Length == 0, "Empty file changed");
        });
        Check("snapshot-content-change-even-with-restored-mtime-rejected", () =>
        {
            var f = Fixture(); var time = File.GetLastWriteTimeUtc(f.File);
            byte[] bytes = File.ReadAllBytes(f.File); bytes[0] ^= 1; File.WriteAllBytes(f.File, bytes); File.SetLastWriteTimeUtc(f.File, time);
            Failure(HandleQuarantine.Stage(f.Snapshot, f.Store), BoundaryError.TargetChanged);
            Require(File.Exists(f.File), "Changed file moved");
        });
        Check("snapshot-file-replacement-rejected", () =>
        {
            var f = Fixture(); File.Move(f.File, f.File + ".original"); File.WriteAllText(f.File, "replacement");
            Failure(HandleQuarantine.Stage(f.Snapshot, f.Store), BoundaryError.TargetChanged);
        });
        Check("snapshot-root-replacement-rejected", () =>
        {
            var f = Fixture(); Directory.Move(f.Root, f.Root + "-old"); Directory.CreateDirectory(f.Root); File.WriteAllText(f.File, "replacement");
            Failure(HandleQuarantine.Stage(f.Snapshot, f.Store), BoundaryError.TargetChanged);
        });
        Check("overlapping-quarantine-rejected", () =>
        {
            var f = Fixture(); string nested = Path.Combine(f.Root, "quarantine"); Directory.CreateDirectory(nested);
            Failure(HandleQuarantine.Stage(f.Snapshot, nested), BoundaryError.OutsideBoundary);
            Failure(HandleQuarantine.Stage(f.Snapshot, f.Base), BoundaryError.OutsideBoundary);
        });
        Check("quarantine-retag-during-commit-never-escapes", () =>
        {
            var f = Fixture(); string outside = Path.Combine(f.Base, "retag-outside"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "untouched");
            MoveOutcome? result = null;
            bool retagged = false;
            try
            {
                result = HandleQuarantine.Stage(f.Snapshot, f.Store, beforeCommit: () => { Reparse(f.Store, outside); retagged = true; });
                Require(Directory.EnumerateFiles(outside).Count() == 1, "Relative rename escaped through retagged directory");
            }
            finally { if (retagged) Reparse(f.Store, null); }
            Require(result != null, "No operation result");
            if (result!.Completed) { Require(File.Exists(result.Receipt!.StoredPath), "Receipt lost original directory object"); Success(HandleQuarantine.Restore(result.Receipt)); }
            else Require(File.Exists(f.File), "Failed operation lost original");
        });
        Check("cancellation-before-commit-leaves-original", () =>
        {
            var f = Fixture(); using var cts = new CancellationTokenSource();
            Failure(HandleQuarantine.Stage(f.Snapshot, f.Store, cts.Token, cts.Cancel), BoundaryError.Cancelled);
            Require(File.Exists(f.File) && !Directory.EnumerateFileSystemEntries(f.Store).Any(), "Cancelled operation mutated a file");
        });
        Check("restore-collision-at-commit-is-atomic-no-overwrite", () =>
        {
            var f = Fixture(); var staged = HandleQuarantine.Stage(f.Snapshot, f.Store); Success(staged);
            var restored = HandleQuarantine.Restore(staged.Receipt!, beforeCommit: () => File.WriteAllText(f.File, "new file"));
            Failure(restored, BoundaryError.DestinationExists);
            Require(File.ReadAllText(f.File) == "new file" && File.Exists(staged.Receipt!.StoredPath), "Collision overwrote data");
            Success(HandleQuarantine.Restore(staged.Receipt!, "restored.txt"));
        });
        Check("restore-modified-quarantine-rejected", () =>
        {
            var f = Fixture(); var staged = HandleQuarantine.Stage(f.Snapshot, f.Store); Success(staged);
            File.AppendAllText(staged.Receipt!.StoredPath, "mutation");
            Failure(HandleQuarantine.Restore(staged.Receipt), BoundaryError.TargetChanged);
        });
        Check("restore-replaced-parent-rejected", () =>
        {
            var f = Fixture(); var staged = HandleQuarantine.Stage(f.Snapshot, f.Store); Success(staged);
            Directory.Move(f.Root, f.Root + "-old"); Directory.CreateDirectory(f.Root);
            Failure(HandleQuarantine.Restore(staged.Receipt!), BoundaryError.TargetChanged);
            Require(File.Exists(staged.Receipt!.StoredPath), "Quarantined file lost");
        });
        Check("restore-cancelled-and-replay-rejected", () =>
        {
            var f = Fixture(); var staged = HandleQuarantine.Stage(f.Snapshot, f.Store); Success(staged);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            Failure(HandleQuarantine.Restore(staged.Receipt!, token: cts.Token), BoundaryError.Cancelled);
            Success(HandleQuarantine.Restore(staged.Receipt!));
            Failure(HandleQuarantine.Restore(staged.Receipt!), BoundaryError.TargetChanged);
        });
        Check("unsupported-recycle-never-deletes-or-falls-back", () =>
        {
            var f = Fixture(); var staged = HandleQuarantine.Stage(f.Snapshot, f.Store); Success(staged);
            Require(!WindowsCapabilities.CanRecycle && !WindowsCapabilities.CanPermanentlyDelete, "Unsafe capability enabled");
            Failure(HandleQuarantine.RequestRecycle(staged.Receipt!), BoundaryError.UnsupportedFeature);
            Require(File.Exists(staged.Receipt!.StoredPath), "Unsupported recycle changed the payload");
            Success(HandleQuarantine.Restore(staged.Receipt));
        });
        Check("repeated-concurrent-leaf-and-parent-swap-while-committing", () =>
        {
            for (int i = 0; i < 40; i++)
            {
                var f = Fixture(); string before = Digest(f.File);
                var result = HandleQuarantine.Stage(f.Snapshot, f.Store, beforeCommit: () =>
                {
                    Task.WaitAll(
                        Task.Run(() => ExpectIoDenied(() => File.Move(f.File, f.File + ".stolen"))),
                        Task.Run(() => ExpectIoDenied(() => Directory.Move(f.Root, f.Root + "-stolen"))),
                        Task.Run(() => ExpectIoDenied(() => Directory.Move(f.Store, f.Store + "-stolen"))),
                        Task.Run(() => ExpectIoDenied(() => File.WriteAllText(f.File, "changed"))));
                });
                Success(result); Require(Digest(result.Receipt!.StoredPath) == before, "Race changed bytes"); Success(HandleQuarantine.Restore(result.Receipt));
            }
        });

        var report = new { timeUtc = DateTimeOffset.UtcNow, os = Environment.OSVersion.VersionString, architecture = RuntimeInformation.ProcessArchitecture.ToString(), failures,
            productionDeletionEnabled = false, recycleEnabled = WindowsCapabilities.CanRecycle, testResults = Results,
            notVerified = new[] { "Real Recycle Bin disabled/quota-full configuration (backend disabled)", "Cross-volume hardware", "Actual cloud provider placeholders", "Writable memory maps", "Crash-persistent ledger and human approval (P3)" } };
        File.WriteAllText(Path.Combine(run, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.GetFullPath("artifacts/p1/latest.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{Results.Count - failures}/{Results.Count} Windows checks passed. Fixtures retained in {run}");
        return failures == 0 ? 0 : 1;
    }

    private static int RestoreWorker(string manifest)
    {
        // Worker only restores this harness's own receipt under artifacts/p1.
        string allowed = Path.GetFullPath("artifacts/p1") + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(manifest);
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) return 2;
        var receipt = JsonSerializer.Deserialize<QuarantineReceipt>(File.ReadAllText(full))!;
        if (!receipt.Original.Path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !receipt.StoredPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) return 2;
        var result = HandleQuarantine.Restore(receipt);
        if (!result.Completed) Console.Error.WriteLine($"Restore worker: {result.Error} native={result.NativeError}");
        return result.Completed ? 0 : 1;
    }

    private static (string Base, string Root, string Store, string File, FileSnapshot Snapshot) Fixture()
    {
        string basis = Path.Combine(run, Guid.NewGuid().ToString("N"));
        string root = Path.Combine(basis, "cache"), store = Path.Combine(basis, "quarantine");
        Directory.CreateDirectory(root); Directory.CreateDirectory(store);
        string file = Path.Combine(root, "fixture.txt"); File.WriteAllText(file, "DEVHARBOR P1 SYNTHETIC " + Guid.NewGuid());
        return (basis, root, store, file, WindowsBoundary.Inspect(root, file));
    }
    private static void Check(string name, Action test)
    {
        try { test(); Results.Add(new { name, status = "passed" }); Console.WriteLine($"PASS {name}"); }
        catch (Exception e) { failures++; Results.Add(new { name, status = "failed", error = e.GetType().Name, detail = e is BoundaryException b ? b.Reason.ToString() : "See local console" }); Console.Error.WriteLine($"FAIL {name}: {e}"); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Success(MoveOutcome result) => Require(result.Completed, $"Expected success, got {result.Error}, native={result.NativeError}");
    private static void Failure(MoveOutcome result, BoundaryError reason) => Require(!result.Completed && result.Error == reason, $"Expected {reason}, got {result.Error}, native={result.NativeError}");
    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Reject(Action action, BoundaryError reason)
    {
        try { action(); } catch (BoundaryException e) when (e.Reason == reason) { return; }
        throw new InvalidOperationException($"Expected rejection: {reason}");
    }
    private static void ExpectIoDenied(Action action)
    {
        try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Concurrent mutation unexpectedly succeeded");
    }
    private static ProcessStartInfo Worker()
    {
        string executable = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        return psi;
    }
    private static void Execute(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi)!;
        if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException("Test worker timed out"); }
        Require(process.ExitCode == 0, $"Test worker exited {process.ExitCode}");
    }
    private static void Junction(string link, string target)
    {
        string command = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) psi.ArgumentList.Add(arg);
        Execute(psi);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int kind, ref uint info, uint length);
    private static void SetCaseSensitive(string path, bool value)
    {
        using var handle = CreateFileW(path, 0x100, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        uint flags = value ? 1u : 0u;
        if (!SetFileInformationByHandle(handle, 23, ref flags, 4)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint code, byte[] input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    private static void Reparse(string path, string? target)
    {
        using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        byte[] data;
        if (target == null) { data = new byte[8]; BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0); }
        else
        {
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            byte[] print = Encoding.Unicode.GetBytes(target);
            data = new byte[16 + substitute.Length + print.Length + 4];
            BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
            BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12);
            BitConverter.GetBytes((ushort)print.Length).CopyTo(data, 14);
            substitute.CopyTo(data, 16); print.CopyTo(data, 16 + substitute.Length + 2);
        }
        if (!DeviceIoControl(handle, target == null ? 0x900ACu : 0x900A4u, data, (uint)data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
