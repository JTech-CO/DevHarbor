using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.Json;
using DevHarbor.Discovery;
using DevHarbor.Windows;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static int failures;
    private static string run = "";
    private static double scanMilliseconds;
    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--worker")
        {
            if (args[1] == "wait") await Task.Delay(10000);
            else if (args[1] == "flood") Console.Write(new string('x', 100000));
            else Console.Write(JsonSerializer.Serialize(args.Skip(1)));
            return 0;
        }
        if (args.FirstOrDefault() == "--probe")
        {
            var tools = await new ToolDiscovery(new CommandRunner(), DiscoveryContext.Current()).DiscoverAsync();
            var safe = tools.Select(t => new { tool = t.Tool.ToString(), version = t.Version, status = t.Status.ToString(), environment = t.Environment.ToString() });
            Directory.CreateDirectory("artifacts/p2");
            File.WriteAllText("artifacts/p2/installed-tools.json", JsonSerializer.Serialize(safe, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(safe, JsonOptions)); return 0;
        }
        run = Path.GetFullPath(Path.Combine("artifacts", "p2", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(run);
        await Check("all-six-adapters-readonly-contract", async () =>
        {
            var fake = new FakeRunner(); var stores = await new ToolDiscovery(fake, Context()).DiscoverAsync();
            Require(stores.Count == 6 && stores.All(s => s.Status == DiscoveryStatus.Ready), "Adapter discovery failed");
            Require(stores.Single(s => s.Tool == ToolKind.Docker).EngineUsage!.Contains("Build Cache: 4MB"), "Engine summary lost");
            Require(fake.Requests.All(r => !r.Arguments.Any(a => a is "prune" or "clean" or "purge" or "install" or "pull" or "rm")), "Mutation requested");
            var df = fake.Requests.Single(r => r.Arguments.Contains("df"));
            Require(df.Arguments[0] == "--host" && df.Environment!["DOCKER_CONTEXT"] == null, "Engine endpoint not pinned");
        });
        await Check("not-installed-does-not-launch-or-install", async () =>
        {
            var fake = new FakeRunner(); var context = Context() with { Resolve = _ => null };
            var stores = await new ToolDiscovery(fake, context).DiscoverAsync();
            Require(stores.All(s => s.Status == DiscoveryStatus.NotInstalled && s.Root == null) && fake.Requests.Count == 0, "Missing tool launched");
        });
        await Check("pip-uv-npm-custom-path-with-spaces", async () =>
        {
            var fake = new FakeRunner { CachePath = Path.Combine(run, "한글 캐시 space") };
            var stores = await new ToolDiscovery(fake, Context()).DiscoverAsync();
            foreach (var kind in new[] { ToolKind.Pip, ToolKind.Uv, ToolKind.Npm }) Require(stores.Single(s => s.Tool == kind).Root == fake.CachePath, "CLI custom path ignored");
        });
        await Check("hf-environment-precedence-and-ollama-no-api-call", async () =>
        {
            var variables = new Dictionary<string,string> { ["HF_HUB_CACHE"] = @"D:\hub-new", ["HUGGINGFACE_HUB_CACHE"] = @"D:\hub-old", ["HF_HOME"] = @"D:\home", ["OLLAMA_MODELS"] = @"D:\models" };
            var fake = new FakeRunner(); var stores = await new ToolDiscovery(fake, Context(variables)).DiscoverAsync();
            Require(stores.Single(s => s.Tool == ToolKind.HuggingFace).Root == @"D:\hub-new", "HF precedence failed");
            Require(stores.Single(s => s.Tool == ToolKind.Ollama).Root == @"D:\models" && fake.Requests.All(r => !r.Executable.EndsWith("ollama.exe")), "Ollama unexpectedly contacted");
            variables.Remove("HF_HUB_CACHE"); variables.Remove("HUGGINGFACE_HUB_CACHE");
            stores = await new ToolDiscovery(fake, Context(variables)).DiscoverAsync();
            Require(stores.Single(s => s.Tool == ToolKind.HuggingFace).Root == @"D:\home\hub", "HF home ignored");
        });
        await Check("ambiguous-or-multiline-cli-path-is-rejected", async () =>
        {
            foreach (string bad in new[] { "relative", "C:\\one\nC:\\two", @"\\server\share\cache", @"D:\cache\..\secrets" })
            {
                var stores = await new ToolDiscovery(new FakeRunner { CachePath = bad }, Context()).DiscoverAsync();
                Require(stores.Single(s => s.Tool == ToolKind.Pip).Status == DiscoveryStatus.InvalidConfiguration, "Ambiguous cache accepted");
            }
        });
        await Check("unknown-version-does-not-prove-layout-or-cleanup", async () =>
        {
            var stores = await new ToolDiscovery(new FakeRunner { Versions = "unknown" }, Context()).DiscoverAsync();
            Require(stores.Single(s => s.Tool == ToolKind.Uv).Version == "미확인", "Version guessed");
            Require(!WindowsCapabilities.CanRecycle && !WindowsCapabilities.CanPermanentlyDelete, "Cleanup enabled");
        });
        await Check("docker-remote-context-never-queries-engine", async () =>
        {
            var fake = new FakeRunner { Endpoint = "ssh://private.example" };
            var stores = await new ToolDiscovery(fake, Context()).DiscoverAsync();
            Require(stores.Single(s => s.Tool == ToolKind.Docker).Status == DiscoveryStatus.RemoteExcluded && fake.Requests.All(r => !r.Arguments.Contains("df")), "Remote engine queried");
        });
        await Check("docker-env-precedence-and-remote-host-guard", async () =>
        {
            var fake = new FakeRunner();
            var stores = await new ToolDiscovery(fake, Context(new() { ["DOCKER_HOST"] = "tcp://127.0.0.1:2375" })).DiscoverAsync();
            Require(stores.Single(s => s.Tool == ToolKind.Docker).Status == DiscoveryStatus.RemoteExcluded && fake.Requests.All(r => !r.Arguments.Contains("df")), "TCP engine contacted");
            fake = new FakeRunner();
            stores = await new ToolDiscovery(fake, Context(new() { ["DOCKER_HOST"] = "ssh://remote", ["DOCKER_CONTEXT"] = "desktop-linux" })).DiscoverAsync();
            Require(stores.Single(s => s.Tool == ToolKind.Docker).Status == DiscoveryStatus.Ready && fake.Requests.Any(r => r.Arguments.Contains("desktop-linux")), "Context precedence wrong");
        });
        await Check("malformed-docker-output-and-command-failure-stay-unknown", async () =>
        {
            var stores = await new ToolDiscovery(new FakeRunner { DockerOutput = "{}" }, Context()).DiscoverAsync();
            Require(stores.Single(s => s.Tool == ToolKind.Docker).Status == DiscoveryStatus.Unavailable, "Invalid engine output accepted");
            stores = await new ToolDiscovery(new FakeRunner { Fail = true }, Context()).DiscoverAsync();
            Require(stores.Where(s => s.Tool != ToolKind.Ollama).All(s => s.Status != DiscoveryStatus.Ready), "Failed tools became ready");
        });
        await Check("discovery-cancellation-preserves-six-status-rows", async () =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel(); var fake = new FakeRunner();
            var stores = await new ToolDiscovery(fake, Context()).DiscoverAsync(cts.Token);
            Require(stores.Count == 6 && fake.Requests.Count == 0 && stores.All(s => s.Root == null), "Cancellation lost rows or launched commands");
        });
        await Check("hardlink-and-parent-child-deduplication", async () =>
        {
            string root = Fixture(); string child = Path.Combine(root, "nested"); Directory.CreateDirectory(child);
            string file = Path.Combine(child, "original"); File.WriteAllBytes(file, new byte[120]);
            if (!CreateHardLinkW(Path.Combine(root, "alias"), file, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = await new StorageScanner().ScanAsync([Store(root), Store(child, "child")]);
            Require(result.UniqueLogicalBytes == 120 && result.Stores.All(s => s.LogicalBytes == 120 && s.SharedLogicalBytes == 120 && s.UniqueFiles == 1), "Shared bytes double counted");
        });
        await Check("corrupt-index-and-hf-without-symlinks-are-metadata-only", async () =>
        {
            string root = Fixture(); string model = Path.Combine(root, "models--fixture", "snapshots", "revision"); Directory.CreateDirectory(model);
            File.WriteAllBytes(Path.Combine(model, "weights.bin"), new byte[13]); File.WriteAllText(Path.Combine(root, "corrupt-index.json"), "{broken");
            var result = await new StorageScanner().ScanAsync([Store(root)]);
            Require(result.UniqueLogicalBytes == 20 && result.Stores[0].Status == ScanStatus.Complete, "Index contents affected metadata scan");
        });
        await Check("handle-enumeration-crosses-buffer-boundaries", async () =>
        {
            string root = Fixture(); for (int i = 0; i < 1500; i++) File.WriteAllBytes(Path.Combine(root, $"fixture-{i:D5}.bin"), new byte[16]);
            var time = Stopwatch.StartNew(); var result = await new StorageScanner().ScanAsync([Store(root)]); scanMilliseconds = time.Elapsed.TotalMilliseconds;
            Require(result.Stores[0].UniqueFiles == 1500 && result.UniqueLogicalBytes == 24000 && !result.IsPartial, "Enumeration lost entries");
        });
        await Check("missing-and-denied-root-not-empty", async () =>
        {
            string root = Fixture(); var dir = new DirectoryInfo(root); var original = dir.GetAccessControl(); var denied = dir.GetAccessControl();
            denied.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            dir.SetAccessControl(denied);
            try
            {
                var result = await new StorageScanner().ScanAsync([Store(root), Store(Path.Combine(run, "missing"), "missing")]);
                Require(result.UniqueLogicalBytes == null && result.Stores.All(s => s.LogicalBytes == null && s.Status == ScanStatus.Partial), "Unreadable became empty");
            }
            finally { dir.SetAccessControl(original); }
        });
        await Check("empty-directory-is-measured-zero", async () =>
        {
            var result = await new StorageScanner().ScanAsync([Store(Fixture())]);
            Require(result.UniqueLogicalBytes == 0 && result.Stores[0].Status == ScanStatus.Complete, "Empty directory unknown");
        });
        await Check("junction-outside-sentinel-never-counted", async () =>
        {
            string root = Fixture(), outside = Fixture(); File.WriteAllBytes(Path.Combine(outside, "sentinel"), new byte[987]);
            Junction(Path.Combine(root, "link"), outside);
            var result = await new StorageScanner().ScanAsync([Store(root)]);
            Require(result.UniqueLogicalBytes == null && result.Stores[0].Issues.Any(i => i.Reason == "ReparsePoint") && File.ReadAllBytes(Path.Combine(outside, "sentinel")).Length == 987, "Junction escaped");
        });
        await Check("offline-entry-excluded-and-readable-part-retained", async () =>
        {
            string root = Fixture(), offline = Path.Combine(root, "offline"); File.WriteAllBytes(offline, new byte[500]); File.WriteAllBytes(Path.Combine(root, "readable"), new byte[5]);
            File.SetAttributes(offline, FileAttributes.Offline);
            try { var result = await new StorageScanner().ScanAsync([Store(root)]); Require(result.UniqueLogicalBytes == 5 && result.IsPartial, "Offline bytes counted"); }
            finally { File.SetAttributes(offline, FileAttributes.Normal); }
        });
        await Check("root-and-enumerated-file-replacement-rejected", () =>
        {
            string root = Fixture(), file = Path.Combine(root, "data"); File.WriteAllText(file, "before");
            var scope = WindowsBoundary.ReadMetadata(root, root); var entry = WindowsBoundary.ReadDirectory(root, root, scope.Identity, scope.Identity).Single();
            File.Move(file, file + ".old"); File.WriteAllText(file, "after");
            Reject(() => WindowsBoundary.ReadScanMetadata(root, file, scope.Identity, entry.Identity));
            Directory.Move(root, root + "-old"); Directory.CreateDirectory(root);
            Reject(() => WindowsBoundary.ReadDirectory(root, root, scope.Identity, scope.Identity).ToArray());
            return Task.CompletedTask;
        });
        await Check("enumeration-pins-directory-and-releases-on-break", () =>
        {
            string root = Fixture(); File.WriteAllText(Path.Combine(root, "file"), "x"); var scope = WindowsBoundary.ReadMetadata(root, root);
            using (var iterator = WindowsBoundary.ReadDirectory(root, root, scope.Identity, scope.Identity).GetEnumerator())
            {
                Require(iterator.MoveNext(), "No fixture"); bool blocked = false;
                try { Directory.Move(root, root + "-moved"); } catch (IOException) { blocked = true; }
                Require(blocked, "Directory could move during enumeration");
            }
            Directory.Move(root, root + "-moved"); return Task.CompletedTask;
        });
        await Check("entry-budget-retains-known-subtotal", async () =>
        {
            string root = Fixture(); for (int i = 0; i < 20; i++) File.WriteAllBytes(Path.Combine(root, $"item{i}"), new byte[10]);
            var result = await new StorageScanner(scanBudget: new(MaxEntries: 3)).ScanAsync([Store(root)]);
            Require(result.IsPartial && result.UniqueLogicalBytes == 30 && result.Stores[0].Issues.Any(i => i.Reason == "ScanBudget"), "Budget lost subtotal");
        });
        await Check("midscan-cancellation-retains-known-subtotal", async () =>
        {
            string root = Fixture(); for (int i = 0; i < 20; i++) File.WriteAllBytes(Path.Combine(root, $"item{i}"), new byte[10]);
            using var cts = new CancellationTokenSource();
            var fs = new CancellingFs(cts); var result = await new StorageScanner(fs).ScanAsync([Store(root)], token: cts.Token);
            Require(result.Stores[0].Status == ScanStatus.Cancelled && result.UniqueLogicalBytes == 30, "Cancellation lost subtotal");
        });
        await Check("engine-values-never-enter-native-file-totals", async () =>
        {
            var result = await new StorageScanner().ScanAsync([new("docker", ToolKind.Docker, null, "fixture", "fixture", Environment: StorageEnvironment.LocalEngine, EngineUsage: "Images: 5GB")]);
            Require(result.UniqueLogicalBytes == null && result.Stores[0].Status == ScanStatus.EngineReported, "Engine counted as physical bytes");
        });
        await Check("command-arguments-preserve-spaces-and-metacharacters", async () =>
        {
            var result = await new CommandRunner().RunAsync(Worker("space ; & $(literal) 한글"), default);
            Require(result.ExitCode == 0 && JsonSerializer.Deserialize<string[]>(result.Output)!.Single() == "space ; & $(literal) 한글", "Argument injection or encoding loss");
        });
        await Check("command-output-timeout-and-cancellation-bounds", async () =>
        {
            await Failure(new CommandRunner(outputLimit: 1024), Worker("flood"), "OutputLimit");
            await Failure(new CommandRunner(TimeSpan.FromMilliseconds(200)), Worker("wait"), "Timeout");
            using var cts = new CancellationTokenSource(200); bool cancelled = false;
            try { await new CommandRunner().RunAsync(Worker("wait"), cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Command cancellation swallowed");
        });
        var evidence = new { timeUtc = DateTimeOffset.UtcNow, os = Environment.OSVersion.VersionString, failures, testResults = Results, fixtureFileCount = 1500, scanMilliseconds, productionDeletionEnabled = false };
        File.WriteAllText(Path.Combine(run, "results.json"), JsonSerializer.Serialize(evidence, JsonOptions));
        File.WriteAllText("artifacts/p2/latest.json", JsonSerializer.Serialize(evidence, JsonOptions));
        Console.WriteLine($"{Results.Count - failures}/{Results.Count} discovery checks passed; 1500-file scan {scanMilliseconds:F0} ms"); return failures == 0 ? 0 : 1;
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string Fixture() { string path = Path.Combine(run, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static StoreDescriptor Store(string path, string id = "fixture") => new(id, ToolKind.Custom, path, "fixture", "Synthetic fixture");
    private static async Task Check(string name, Func<Task> test)
    {
        try { await test(); Results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Results.Add(new { name, status = "failed", error = e.GetType().Name }); Console.Error.WriteLine($"FAIL {name}: {e}"); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (BoundaryException e) when (e.Reason == BoundaryError.TargetChanged) { return; } throw new InvalidOperationException("Expected TargetChanged"); }
    private static async Task Failure(CommandRunner runner, CommandRequest request, string reason)
    { try { await runner.RunAsync(request, default); } catch (CommandFailure e) when (e.Reason == reason) { return; } throw new InvalidOperationException("Expected " + reason); }
    private static CommandRequest Worker(string argument)
    {
        string executable = Environment.ProcessPath!;
        string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [typeof(Program).Assembly.Location] : [];
        return new(executable, [.. prefix, "--worker", argument], Environment.CurrentDirectory);
    }
    private static DiscoveryContext Context(Dictionary<string,string>? vars = null) => new(run, vars ?? [], name => Path.Combine(run, name + ".exe"), _ => "0.17.7");
    private sealed class FakeRunner : ICommandRunner
    {
        public List<CommandRequest> Requests { get; } = [];
        public string CachePath = @"D:\fixture cache";
        public string Endpoint = "npipe:////./pipe/docker_engine";
        public string? Versions;
        public bool Fail;
        public string DockerOutput = string.Join('\n', new[] { "Images", "Containers", "Local Volumes", "Build Cache" }.Select((t,i) => JsonSerializer.Serialize(new { Type = t, Size = $"{i+1}MB" })));
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken token)
        {
            Requests.Add(request); if (Fail) throw new CommandFailure("Timeout");
            var a = request.Arguments;
            string value = a.Contains("df") ? DockerOutput : a.Contains("inspect") ? JsonSerializer.Serialize(Endpoint)
                : a.Contains("show") ? "default" : a.Contains("dir") || a.Contains("get") ? CachePath
                : a.Contains("-c") ? "1.4.1" : Versions ?? (request.Executable.Contains("python") ? "pip 26.2.1" : request.Executable.Contains("uv") ? "uv 0.11.0" : request.Executable.Contains("node") ? "11.12.0" : "Docker version 29.3.0");
            return Task.FromResult(new CommandResult(0, value, ""));
        }
    }
    private sealed class CancellingFs(CancellationTokenSource cancellation) : IStorageFileSystem
    {
        private readonly WindowsStorageFileSystem inner = new(); private int count;
        public EntryMetadata Root(string root, CancellationToken token) => inner.Root(root, token);
        public EntryMetadata Read(string root, string path, FileIdentity scope, FileIdentity expected, CancellationToken token)
        { var data = inner.Read(root,path,scope,expected,token); if (++count == 3) cancellation.Cancel(); return data; }
        public IEnumerable<DirectoryEntry> Children(string root,string path,FileIdentity scope,FileIdentity expected,CancellationToken token) => inner.Children(root,path,scope,expected,token);
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateHardLinkW(string name,string existing,IntPtr security);
    private static void Junction(string link,string target)
    {
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute=false, CreateNoWindow=true };
        string command = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' | Out-Null";
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException(); } Require(process.ExitCode==0,"Junction fixture failed");
    }
}
