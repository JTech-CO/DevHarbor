using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DevHarbor.Models;
namespace DevHarbor.Desktop;
public partial class ModelSessionsWindow : Window
{
    private readonly OllamaSessions sessions;
    private bool busy;
    private ModelObservation? observation;
    private CancellationTokenSource? cancellation;
    private readonly ObservableCollection<string> history = [];
    public ModelSessionsWindow() : this(new OllamaSessions()) { }
    internal ModelSessionsWindow(OllamaSessions sessions)
    {
        this.sessions = sessions; InitializeComponent(); HistoryList.ItemsSource = history;
        Closing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); StatusText.Text = "중지를 요청했습니다. 전송된 언로드 요청은 취소 결과를 보장할 수 없어 상태 확인이 필요합니다."; } };
        Closed += (_, _) => sessions.Dispose();
    }
    internal void Present(ModelObservation result)
    {
        observation = result; ModelsTable.ItemsSource = result.Models.Select(m => new ModelRow(m)).ToArray();
        MemoryText.Text = "Ollama " + result.Version + " · 프로세스 working set 합계 " + StoreRow.Size(result.WorkingSetBytes) + " · RAM 미측정\nAPI 모델 크기와 VRAM은 서버 보고값입니다. working set은 공유 페이지 중복을 포함할 수 있으며 회수량이 아닙니다.";
        StatusText.Text = result.Status == "Ready" ? $"{result.Models.Count}개 로드 모델 · {result.ObservedAt.ToLocalTime():HH:mm:ss} 관측" : "조회 제한: " + result.Status + " · 지원 버전 " + OllamaSessions.VerifiedVersion;
        UpdateButtons();
    }
    private void UpdateButtons() { RefreshButton.IsEnabled = !busy; UnloadButton.IsEnabled = !busy && observation?.Status == "Ready" && ModelsTable.SelectedItem is ModelRow; }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) UpdateButtons(); }
    private async void Refresh(object sender, RoutedEventArgs e)
    {
        if (busy) return; busy = true; cancellation = new(); UpdateButtons();
        try { Present(await sessions.ObserveAsync(cancellation.Token)); }
        catch (OperationCanceledException) { StatusText.Text = "조회가 취소되었습니다."; }
        finally { cancellation.Dispose(); cancellation = null; busy = false; UpdateButtons(); }
    }
    private async void Unload(object sender, RoutedEventArgs e)
    {
        if (busy || ModelsTable.SelectedItem is not ModelRow row) return;
        busy = true; cancellation = new(); UpdateButtons();
        try
        {
            var plan = await sessions.PrepareUnloadAsync(row.Model, cancellation.Token);
            var dialog = new UnloadApprovalWindow(sessions, plan) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Grant == null) { StatusText.Text = "언로드 승인을 취소했습니다. 요청을 보내지 않았습니다."; return; }
            var result = await sessions.UnloadAsync(plan, dialog.Grant, cancellation.Token);
            history.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss} · {row.Name} · {ResultLabel(result.Status)}" + (result.Reason == null ? "" : " (" + result.Reason + ")"));
            StatusText.Text = ResultLabel(result.Status) + " · 자동 재시도하지 않습니다. 로드 상태 조회로 다시 확인하세요.";
            // Observations predating a mutation are no longer actionable.
            observation = null;
        }
        catch (ModelSessionException error) { StatusText.Text = "언로드 차단: " + error.Code; }
        catch (OperationCanceledException) { StatusText.Text = "언로드 준비가 취소되었습니다."; }
        finally { cancellation.Dispose(); cancellation = null; busy = false; UpdateButtons(); }
    }
    internal static string ResultLabel(string state) => state switch { "Unloaded" => "언로드 후 부재 확인", "AlreadyAbsent" => "이미 로드되어 있지 않음", "StillLoaded" => "아직 로드됨 / 다시 로드됨", "OutcomeUnknown" => "요청 결과 미확정", _ => "요청 차단" };
    internal sealed class ModelRow(LoadedModel model)
    {
        internal LoadedModel Model => model;
        public string Name => model.Name;
        public string Size => StoreRow.Size(model.ModelBytes);
        public string Vram => StoreRow.Size(model.VramBytes);
        public string Expires => model.ExpiresAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    }
}
