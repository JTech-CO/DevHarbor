using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevHarbor.Execution;
namespace DevHarbor.Desktop;
public partial class CleanupWindow : Window
{
    private readonly WorkflowService service;
    private bool busy;
    private CancellationTokenSource? cancellation;
    internal CleanupWindow(WorkflowService service, CleanupAssessment? assessment)
    {
        this.service = service; InitializeComponent();
        AssessmentText.Text = assessment == null ? "저장소 지도에서 항목을 선택하면 차단 근거를 확인할 수 있습니다. 실제 캐시 정리와 자동 재생성은 아직 실행할 수 없습니다."
            : assessment.Tool + " · " + assessment.Root + "\n" + string.Join("\n", assessment.Reasons);
        Loaded += async (_, _) => await Reload();
        Closing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); StatusText.Text = "중지를 요청했습니다. 파일 상태와 기록이 확정될 때까지 기다려 주세요."; } };
        Closed += (_, _) => { cancellation?.Dispose(); service.Dispose(); };
    }
    private void SetBusy(bool value)
    {
        busy = value; CreateButton.IsEnabled = RefreshButton.IsEnabled = AlternateName.IsEnabled = !value;
        StopButton.IsEnabled = value && cancellation != null; UpdateSelection();
    }
    internal void PresentHistory(IReadOnlyList<HistoryEntry> entries)
    {
        HistoryTable.ItemsSource = entries.Select(e => new HistoryRow(e)).ToArray();
        StatusText.Text = entries.Count == 0 ? "아직 기록된 파일 이동이 없습니다." : $"{entries.Count}개 작업 기록을 확인했습니다. 자동으로 파일을 이동하거나 재실행하지 않습니다.";
        UpdateSelection();
    }
    private async Task Reload()
    {
        if (busy) return; SetBusy(true);
        try { PresentHistory(await Task.Run(service.Reconcile)); }
        catch (Exception e) when (Expected(e)) { ShowFailure(e); }
        finally { SetBusy(false); }
    }
    private async Task ReviewAndExecute(Func<CleanupPlan> create)
    {
        if (busy) return; SetBusy(true);
        try
        {
            var plan = await Task.Run(create);
            var dialog = new ApprovalWindow(plan, service.Broker) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Grant == null) { StatusText.Text = "승인을 취소했습니다. 파일 이동은 실행하지 않았습니다."; return; }
            cancellation = new(); SetBusy(true); StatusText.Text = "승인과 파일 상태를 재검사하고 결과를 기록하고 있습니다.";
            var result = await Task.Run(() => service.Execute(plan, dialog.Grant, cancellation.Token));
            PresentHistory(await Task.Run(service.ReadHistory));
            StatusText.Text = StateLabel(result.State) + (result.Reason == null ? "" : " · " + ReasonLabel(result.Reason));
        }
        catch (Exception e) when (Expected(e)) { ShowFailure(e); }
        finally { cancellation?.Dispose(); cancellation = null; SetBusy(false); }
    }
    private async void CreateSample(object sender, RoutedEventArgs e) => await ReviewAndExecute(service.CreateSamplePlan);
    private async void Restore(object sender, RoutedEventArgs e)
    {
        if (HistoryTable.SelectedItem is not HistoryRow row || !row.CanRestore) return;
        string? leaf = string.IsNullOrWhiteSpace(AlternateName.Text) ? null : AlternateName.Text.Trim();
        await ReviewAndExecute(() => service.CreateRestorePlan(row.Entry.Id, leaf));
    }
    private async void Refresh(object sender, RoutedEventArgs e) => await Reload();
    private void Stop(object sender, RoutedEventArgs e) { cancellation?.Cancel(); StatusText.Text = "중지 요청을 보냈습니다. 이미 완료된 이동은 이력에 기록됩니다."; }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) UpdateSelection(); }
    private void UpdateSelection()
    {
        var row = HistoryTable.SelectedItem as HistoryRow; RestoreButton.IsEnabled = !busy && row?.CanRestore == true;
        DetailText.Text = row == null ? "보관 중인 항목을 선택하면 복원을 검토할 수 있습니다." : "원래 위치: " + row.Entry.Source + "\n도착 위치: " + row.Entry.Destination + (row.Entry.Reason == null ? "" : "\n" + ReasonLabel(row.Entry.Reason));
    }
    private static bool Expected(Exception e) => e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException;
    private void ShowFailure(Exception e) => StatusText.Text = "처리를 완료하지 못했습니다. 이동 여부는 상태 다시 확인으로 대조하세요. " + ReasonLabel(e is WorkflowException w ? w.Reason : e is Microsoft.Data.Sqlite.SqliteException ? "LedgerUnavailable" : "StateUnavailable");
    internal static string StateLabel(string value) => value switch { "Held" => "보관 중", "Restored" => "복원 완료", "NotApplied" => "이동 안 됨", "NeedsReview" => "직접 확인 필요", "Intent" or "RecoveryRequired" => "상태 대조 필요", "Blocked" => "실행 차단", _ => "확인 필요" };
    private static string ReasonLabel(string value) => value switch
    {
        "DestinationExists" => "같은 이름의 파일이 있습니다. 다른 복원 이름으로 새 승인을 진행하세요.",
        "Expired" => "승인이 만료되었습니다. 새 계획을 만드세요.",
        "TargetChanged" => "파일이 달라져 이동을 중단했습니다.",
        "LedgerUnavailable" => "기록 저장소를 열거나 확정할 수 없습니다. 이력 파일을 보존한 상태로 확인이 필요합니다.",
        "LedgerMissing" => "원장 파일이 없습니다. 새로 만들지 않고 실행을 차단했습니다. 기존 기록을 먼저 확인하세요.",
        "RecoveryRequired" => "상태 다시 확인을 눌러 중단된 작업을 대조하세요.",
        "ConcurrentOperation" => "다른 작업이 진행 중입니다. 완료 후 다시 시도하세요.",
        "AmbiguousFilesystemState" => "파일 상태가 기록과 다릅니다. 자동 복원하지 않습니다.",
        "Cancelled" => "중지 요청을 처리했습니다.", "Reconciled" => "기록과 실제 파일 상태를 대조했습니다.",
        _ => "안전 조건을 확인할 수 없습니다. 확인 코드: " + value
    };
    internal sealed class HistoryRow(HistoryEntry entry)
    {
        internal HistoryEntry Entry => entry;
        internal bool CanRestore => entry.Action == nameof(WorkflowAction.HoldSample) && entry.State == "Held";
        public string Action => entry.Action == nameof(WorkflowAction.HoldSample) ? "보관" : "복원";
        public string State => StateLabel(entry.State);
        public string Source => entry.Source;
        public string Time => entry.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    }
}
