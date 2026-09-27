using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DevHarbor.AgentBridge;
namespace DevHarbor.Desktop;
public partial class ConnectionsWindow : Window
{
    private readonly AgentConnection connection;
    private readonly StorageViewModel model;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal ConnectionsWindow(AgentConnection connection, StorageViewModel model)
    {
        this.connection = connection; this.model = model; InitializeComponent();
        var launch = McpLaunch.Resolve();
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        ConfigText.Text = "Claude Code / Cursor (JSON)\n" + JsonSerializer.Serialize(new { mcpServers = new { devharbor = new { type = "stdio", command = launch.Command, args = launch.Arguments } } }, json)
            + "\n\nCodex (config.toml)\n[mcp_servers.devharbor]\ncommand = " + JsonSerializer.Serialize(launch.Command, json) + "\nargs = " + JsonSerializer.Serialize(launch.Arguments, json) + "\n";
        timer.Tick += (_, _) => RefreshView(); Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop(); RefreshView();
    }
    private void RefreshView()
    {
        SharingStatus.Text = connection.Hub.IsSharing ? "공유 중 · 같은 Windows 사용자 세션만 연결" : "공유 꺼짐 · 스캔 결과를 제공하지 않습니다";
        SharingButton.Content = connection.Hub.IsSharing ? "공유 중지" : "스캔 결과 공유 시작";
        string? selected = (PlanTable.SelectedItem as AgentPlan)?.Id;
        var rows = connection.Hub.Plans(); PlanTable.ItemsSource = rows; PlanTable.SelectedItem = rows.FirstOrDefault(p => p.Id == selected);
    }
    private async void ToggleSharing(object sender, RoutedEventArgs e)
    {
        SharingButton.IsEnabled = false;
        try
        {
            if (connection.Hub.IsSharing) await connection.Disable(); else connection.Enable(model.CurrentReport);
            StatusText.Text = connection.Hub.IsSharing ? "공유를 시작했습니다. 스캔 전이라면 저장소 지도에서 먼저 스캔하세요." : "공유를 중지하고 이 세션의 계획과 항목 ID를 폐기했습니다.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusText.Text = "연결을 열 수 없습니다. 다른 DevHarbor 창의 공유 상태를 확인하세요."; }
        finally { SharingButton.IsEnabled = true; RefreshView(); }
    }
    private void ReviewPlan(object sender, RoutedEventArgs e)
    {
        if (PlanTable.SelectedItem is not AgentPlan plan) return;
        string details = string.Join("\n\n", plan.Items.Select(i => i.Tool + " · " + i.Root + "\n" + string.Join("\n", i.Reasons)));
        MessageBox.Show(this, "실제 캐시 정리 조건이 검증되지 않아 실행할 수 없습니다. 이 확인은 실행 승인이 아닙니다.\n\n" + details, "계획 차단 근거", MessageBoxButton.OK, MessageBoxImage.Information);
        connection.Hub.MarkReviewed(plan.Id); RefreshView();
    }
}
