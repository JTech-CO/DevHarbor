using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using DevHarbor.Discovery;

namespace DevHarbor.Desktop;

public sealed class StoreRow(StoreMeasurement item)
{
    public StoreMeasurement Measurement => item;
    public string Tool => item.Store.Tool switch { ToolKind.HuggingFace => "Hugging Face", ToolKind.Custom => "사용자 경로", _ => item.Store.Tool.ToString() };
    public string Drive => item.Store.Root is { } root && Path.IsPathFullyQualified(root) ? Path.GetPathRoot(root)! : "해당 없음";
    public string Environment => item.Store.Environment switch { StorageEnvironment.NativeLocal => "Windows 로컬", StorageEnvironment.LocalEngine => "Docker 엔진", _ => "원격 (제외)" };
    public string Logical => Size(item.LogicalBytes, item.Status);
    public string Allocated => Size(item.AllocatedBytes, item.Status);
    public string Shared => Size(item.SharedLogicalBytes, item.Status);
    public string Status => item.Store.Status switch
    {
        DiscoveryStatus.NotInstalled => "미설치 / 실행 파일 없음",
        DiscoveryStatus.RemoteExcluded => "원격 조회 제외",
        DiscoveryStatus.InvalidConfiguration => "경로 설정 확인 필요",
        DiscoveryStatus.Unavailable => "조회 불가",
        _ => item.Status switch { ScanStatus.Complete => "측정 완료", ScanStatus.Partial => "부분 측정", ScanStatus.Cancelled => "취소 · 부분 결과", ScanStatus.EngineReported => "엔진 보고", _ => "미측정" }
    };
    public string Title => $"{Tool} · 버전 {item.Store.Version} · {Status}";
    public string Root => item.Store.Root ?? "로컬 파일 경로 없음";
    public string Evidence => item.Store.Evidence;
    public string Detail => item.Store.EngineUsage ?? $"파일 {item.UniqueFiles:N0}개 · 제외/오류 {item.SkippedEntries:N0}개 · {item.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
        (item.Issues.Count > 0 ? " · " + string.Join(", ", item.Issues.Select(i => IssueLabel(i.Reason)).Distinct()) : "");
    private static string IssueLabel(string reason) => reason switch
    {
        "AccessDenied" => "접근 권한 없음", "TargetChanged" => "경로 변경 또는 사라짐",
        "ReparsePoint" => "링크 경로 제외", "UnsupportedFeature" => "지원하지 않는 파일 상태",
        "ScanBudget" => "조사 시간·항목 한도 도달", "DepthBudget" => "폴더 깊이 한도 도달",
        "ProtectedPath" => "보호 경로 제외", "Busy" => "잠긴 경로", "InvalidPath" => "경로 확인 필요",
        _ => "파일 정보 조회 실패"
    };
    public static string Size(long? bytes, ScanStatus status = ScanStatus.Complete)
    {
        if (bytes == null) return "미측정";
        double value = bytes.Value; string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (status is ScanStatus.Partial or ScanStatus.Cancelled ? "≥ " : "") + value.ToString(unit == 0 ? "N0" : "N1") + " " + units[unit];
    }
}

public sealed class ActionCommand(Action action, Func<bool> enabled) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => enabled();
    public void Execute(object? parameter) { if (enabled()) action(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class StorageViewModel : INotifyPropertyChanged
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<StoreDescriptor>>> discover;
    private readonly StorageScanner scanner;
    private readonly List<StoreDescriptor> custom = [];
    private CancellationTokenSource? cancellation;
    private ScanReport? report;
    private string statusText = "스캔은 버튼을 눌렀을 때 시작됩니다. 파일 내용은 읽지 않습니다.";
    private string driveFilter = "전체 드라이브", environmentFilter = "전체 환경", customPath = "";
    private StoreRow? selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ScanReport>? ScanCompleted;
    public ScanReport? CurrentReport => report;
    public ObservableCollection<StoreRow> AllRows { get; } = [];
    public ICollectionView Rows { get; }
    public ObservableCollection<string> Drives { get; } = ["전체 드라이브"];
    public string[] Environments { get; } = ["전체 환경", "Windows 로컬", "Docker 엔진", "원격 (제외)"];
    public ActionCommand ScanCommand { get; }
    public ActionCommand CancelCommand { get; }
    public ActionCommand AddPathCommand { get; }
    public bool IsBusy => cancellation != null;
    public string StatusText { get => statusText; private set { statusText = value; Changed(); } }
    public string DriveFilter { get => driveFilter; set { driveFilter = value; Changed(); Rows.Refresh(); } }
    public string EnvironmentFilter { get => environmentFilter; set { environmentFilter = value; Changed(); Rows.Refresh(); } }
    public string CustomPath { get => customPath; set { customPath = value; Changed(); AddPathCommand.Refresh(); } }
    public StoreRow? Selected { get => selected; set { selected = value; Changed(); } }
    public string AllocatedSummary => StoreRow.Size(report?.UniqueAllocatedBytes, report?.IsPartial == true ? ScanStatus.Partial : ScanStatus.Complete);
    public string LogicalSummary => StoreRow.Size(report?.UniqueLogicalBytes, report?.IsPartial == true ? ScanStatus.Partial : ScanStatus.Complete);
    public string CoverageSummary => report == null ? "스캔 대기" : $"{report.Stores.Count(s => s.LogicalBytes != null)} / {report.Stores.Count}개 저장소";
    public string CoverageNote => report == null ? "미설치·오류·취소를 0으로 계산하지 않음" : $"미측정 {report.UnmeasuredStores}개 · {(report.IsPartial ? "부분 결과" : "조회 종료")}";
    public StorageViewModel(Func<CancellationToken, Task<IReadOnlyList<StoreDescriptor>>>? discovery = null, StorageScanner? storageScanner = null)
    {
        discover = discovery ?? new ToolDiscovery(new CommandRunner(), DiscoveryContext.Current()).DiscoverAsync;
        scanner = storageScanner ?? new();
        Rows = CollectionViewSource.GetDefaultView(AllRows);
        Rows.Filter = value => value is StoreRow row && (DriveFilter == "전체 드라이브" || row.Drive == DriveFilter)
            && (EnvironmentFilter == "전체 환경" || row.Environment == EnvironmentFilter);
        ScanCommand = new(async () => await ScanAsync(), () => !IsBusy);
        CancelCommand = new(Cancel, () => IsBusy);
        AddPathCommand = new(AddPath, () => !IsBusy && !string.IsNullOrWhiteSpace(CustomPath) && custom.Count < 20);
    }
    private void AddPath()
    {
        string path = CustomPath.Trim().TrimEnd('\\');
        if (!Path.IsPathFullyQualified(path) || path.Length < 4 || path.StartsWith(@"\\") || path.Any(char.IsControl))
        { StatusText = "도구 저장소의 로컬 절대 경로를 입력하세요. 예: D:\\Caches\\uv"; return; }
        if (custom.Any(s => string.Equals(s.Root, path, StringComparison.OrdinalIgnoreCase))) { StatusText = "이미 추가한 경로입니다."; return; }
        custom.Add(new("custom-" + custom.Count, ToolKind.Custom, path, "미확인", "사용자가 지정한 범위 · 소유 도구/복구 가능성 미검증"));
        CustomPath = ""; StatusText = $"사용자 경로 {custom.Count}개 추가됨. 다음 스캔에 포함됩니다.";
    }
    public async Task ScanAsync()
    {
        if (IsBusy) return;
        using var cts = new CancellationTokenSource(); cancellation = cts; RefreshCommands();
        try
        {
            StatusText = "설치된 도구의 캐시 경로를 확인하는 중…";
            var stores = await Task.Run(() => discover(cts.Token));
            var progress = new Progress<ScanProgress>(p => { if (cancellation == cts) StatusText = $"{p.StoreId} 조사 중 · {p.Entries:N0}개 항목 · 중지하면 지금까지의 결과를 남깁니다."; });
            report = await scanner.ScanAsync([.. stores, .. custom], progress, cts.Token);
            AllRows.Clear(); foreach (var result in report.Stores) AllRows.Add(new(result));
            ScanCompleted?.Invoke(report);
            string previousDrive = DriveFilter;
            Drives.Clear(); Drives.Add("전체 드라이브"); foreach (string drive in AllRows.Select(r => r.Drive).Where(d => d != "해당 없음").Distinct().Order()) Drives.Add(drive);
            DriveFilter = Drives.Contains(previousDrive) ? previousDrive : "전체 드라이브";
            Selected = Rows.Cast<StoreRow>().FirstOrDefault();
            StatusText = $"{(cts.IsCancellationRequested ? "스캔 중지 · 부분 결과 보존" : report.IsPartial ? "조사 종료 · 제외/오류가 있는 부분 결과" : "조사 완료")} · {report.CompletedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 상단 합계는 모든 필터의 로컬 파일 기준입니다.";
            foreach (string name in new[] { nameof(AllocatedSummary), nameof(LogicalSummary), nameof(CoverageSummary), nameof(CoverageNote) }) Changed(name);
        }
        catch (OperationCanceledException) { StatusText = "조회가 취소되었습니다. 이전 결과를 유지합니다."; }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { StatusText = "조사를 완료할 수 없습니다. 이전 결과를 유지합니다. (" + e.GetType().Name + ")"; }
        finally { cancellation = null; RefreshCommands(); }
    }
    public void Cancel() => cancellation?.Cancel();
    private void RefreshCommands() { Changed(nameof(IsBusy)); ScanCommand.Refresh(); CancelCommand.Refresh(); AddPathCommand.Refresh(); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
