using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevHarbor.Desktop;
using DevHarbor.Discovery;
using DevHarbor.Windows;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static int failures;
    private static double maximumUiGap, cancelMilliseconds, fullScanMilliseconds;
    private static int ticks;
    [STAThread]
    private static int Main()
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DevHarbor.Desktop;component/Theme.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(); }
            catch (Exception e) { failures++; Console.Error.WriteLine(e); }
            finally { app.Shutdown(); }
        }));
        app.Run(); return failures == 0 ? 0 : 1;
    }
    private static async Task Run()
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "p2", "desktop-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        for (int i = 0; i < 1500; i++) File.WriteAllBytes(Path.Combine(root, $"fixture-{i}.bin"), new byte[16]);
        IReadOnlyList<StoreDescriptor> stores = [new("pip", ToolKind.Pip, root, "fixture", "pip cache dir · 합성 fixture"),
            new("uv", ToolKind.Uv, root, "fixture", "uv cache dir · 중첩 fixture"),
            new("npm", ToolKind.Npm, null, "미확인", "실행 파일 없음", DiscoveryStatus.NotInstalled),
            new("ollama", ToolKind.Ollama, null, "미확인", "원격 조회하지 않음", DiscoveryStatus.RemoteExcluded, StorageEnvironment.Remote),
            new("hf", ToolKind.HuggingFace, Path.Combine(root, "missing"), "fixture", "손실된 경로 fixture"),
            new("docker", ToolKind.Docker, null, "fixture", "로컬 엔진 fixture · 호스트 합계에서 제외", Environment: StorageEnvironment.LocalEngine, EngineUsage: "Images: 12GB · Containers: 0B · Local Volumes: 2GB · Build Cache: 400MB")];
        var model = new StorageViewModel(_ => Task.FromResult(stores));
        var window = new MainWindow(model);
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        await Check("empty-state-and-no-automatic-scan", () =>
        {
            Require(model.AllRows.Count == 0 && !model.IsBusy && model.ScanCommand.CanExecute(null) && !model.CancelCommand.CanExecute(null), "Initial state wrong");
            Render(window, "artifacts/p2/ui-empty.png", 1360, 880); return Task.CompletedTask;
        });
        await Check("large-fixture-ui-responsive-and-results-bound", async () =>
        {
            var clock = Stopwatch.StartNew(); double previous = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { double now = clock.Elapsed.TotalMilliseconds; maximumUiGap = Math.Max(maximumUiGap, now - previous); previous = now; ticks++; };
            timer.Start(); var scan = model.ScanAsync();
            Require(model.IsBusy && !model.ScanCommand.CanExecute(null) && model.CancelCommand.CanExecute(null), "Commands did not switch");
            await scan; timer.Stop(); fullScanMilliseconds = clock.Elapsed.TotalMilliseconds;
            Require(!model.IsBusy && model.AllRows.Count == 6 && ticks > 3 && maximumUiGap < 1000, "UI blocked or results missing");
            Require(model.AllRows.First().Measurement.UniqueFiles == 1500 && model.AllocatedSummary != "미측정", "Results not bound");
            Render(window, "artifacts/p2/ui-results.png", 1360, 880);
        });
        await Check("drive-environment-filters-and-selection", () =>
        {
            model.EnvironmentFilter = "Docker 엔진"; Require(model.Rows.Cast<StoreRow>().Single().Tool == "Docker", "Environment filter broken");
            model.EnvironmentFilter = "전체 환경"; model.DriveFilter = Path.GetPathRoot(root)!;
            Require(model.Rows.Cast<StoreRow>().Count() == 3, "Drive filter broken");
            model.DriveFilter = "전체 드라이브"; model.Selected = model.AllRows.Last();
            Render(window, "artifacts/p2/ui-minimum.png", 1080, 760);
            Require(model.Selected.Detail.Contains("Images: 12GB"), "Engine evidence not displayed");
            return Task.CompletedTask;
        });
        await Check("custom-path-validation-and-explicit-scan", () =>
        {
            model.CustomPath = "relative"; model.AddPathCommand.Execute(null); Require(model.StatusText.Contains("절대 경로"), "Invalid custom path accepted");
            model.CustomPath = root; model.AddPathCommand.Execute(null); Require(model.StatusText.Contains("1개") && !model.IsBusy, "Add unexpectedly scans");
            model.CustomPath = root; model.AddPathCommand.Execute(null); Require(model.StatusText.Contains("이미"), "Duplicate path accepted");
            return Task.CompletedTask;
        });
        await Check("cancel-returns-partial-results-and-enables-rescan", async () =>
        {
            var slow = new SlowFs();
            var cancelModel = new StorageViewModel(_ => Task.FromResult<IReadOnlyList<StoreDescriptor>>([stores[0]]), new StorageScanner(slow));
            var scan = cancelModel.ScanAsync();
            await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); var clock = Stopwatch.StartNew(); cancelModel.CancelCommand.Execute(null); await scan; cancelMilliseconds = clock.Elapsed.TotalMilliseconds;
            Require(cancelMilliseconds < 1000 && cancelModel.ScanCommand.CanExecute(null) && !cancelModel.CancelCommand.CanExecute(null), "Cancellation unresponsive");
            Require(cancelModel.AllRows.Single().Measurement.Status == ScanStatus.Cancelled && cancelModel.AllRows.Single().Measurement.UniqueFiles > 0, "Partial rows lost");
        });
        await Check("no-mutation-controls-or-binding-errors", () =>
        {
            var buttons = Descendants(window.Content as DependencyObject).OfType<Button>().Where(b => b.Command is ActionCommand).ToArray();
            Require(buttons.Length == 3 && buttons.All(b => b.Command is ActionCommand), "Unexpected action surface");
            Require(errors.Messages.Count == 0, "WPF binding errors: " + string.Join(";", errors.Messages)); return Task.CompletedTask;
        });
        window.Close();
        var evidence = new { timeUtc = DateTimeOffset.UtcNow, os = Environment.OSVersion.VersionString, failures, testResults = Results,
            fixtureFileCount = 1500, fullScanMilliseconds, dispatcherTicks = ticks, maximumUiGapMilliseconds = maximumUiGap, cancelMilliseconds,
            visualReview = "WPF RenderTargetBitmap; interactive manual user review not claimed" };
        File.WriteAllText("artifacts/p2/desktop-latest.json", JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{Results.Count - failures}/{Results.Count} desktop checks passed; scan={fullScanMilliseconds:F0}ms, max UI gap={maximumUiGap:F0}ms, cancel={cancelMilliseconds:F0}ms");
    }
    private static void Render(MainWindow window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject? value)
    {
        if (value == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
        { var child = VisualTreeHelper.GetChild(value, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static async Task Check(string name, Func<Task> test)
    {
        try { await test(); Results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Results.Add(new { name, status = "failed", error = e.GetType().Name }); Console.Error.WriteLine($"FAIL {name}: {e}"); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class SlowFs : IStorageFileSystem
    {
        private readonly WindowsStorageFileSystem inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;
        public EntryMetadata Root(string root,CancellationToken token) => inner.Root(root,token);
        public EntryMetadata Read(string root,string path,FileIdentity scope,FileIdentity expected,CancellationToken token)
        { Thread.Sleep(4); var result = inner.Read(root,path,scope,expected,token); if (++count == 3) Started.TrySetResult(); return result; }
        public IEnumerable<DirectoryEntry> Children(string root,string path,FileIdentity scope,FileIdentity expected,CancellationToken token) => inner.Children(root,path,scope,expected,token);
    }
}
