using System.Windows;
using DevHarbor.Execution;
namespace DevHarbor.Desktop;
public partial class MainWindow : Window
{
    private readonly AgentConnection connection = new();
    private bool closingAfterDisconnect;
    public MainWindow() : this(new StorageViewModel()) { }
    public MainWindow(StorageViewModel model)
    {
        InitializeComponent(); DataContext = model;
        model.ScanCompleted += connection.Hub.Publish;
        Closing += async (_, e) =>
        {
            model.Cancel();
            if (!connection.Hub.IsSharing) { model.ScanCompleted -= connection.Hub.Publish; return; }
            if (!closingAfterDisconnect)
            {
                e.Cancel = true; closingAfterDisconnect = true;
                await connection.DisposeAsync(); model.ScanCompleted -= connection.Hub.Publish;
                await Dispatcher.InvokeAsync(Close);
            }
        };
    }
    private void OpenModels(object sender, RoutedEventArgs e) => new ModelSessionsWindow { Owner = this }.ShowDialog();
    private void OpenConnections(object sender, RoutedEventArgs e) => new ConnectionsWindow(connection, (StorageViewModel)DataContext) { Owner = this }.ShowDialog();
    private async void OpenCleanup(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender; button.IsEnabled = false;
        try
        {
            var selected = (DataContext as StorageViewModel)?.Selected;
            var assessment = selected == null ? null : CleanupEligibility.Assess(selected.Measurement);
            var service = await Task.Run(WorkflowService.OpenDesktop);
            try { new CleanupWindow(service, assessment) { Owner = this }.ShowDialog(); }
            finally { service.Dispose(); }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { MessageBox.Show(this, "복구 기록 저장소를 열 수 없습니다. 기록과 파일을 보존한 상태로 경로·권한·다른 실행 작업을 확인하세요.", "정리·복구 실행 차단", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { button.IsEnabled = true; }
    }
}
