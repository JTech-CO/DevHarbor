using System.Windows;
using System.Windows.Threading;
using DevHarbor.Models;
namespace DevHarbor.Desktop;
public partial class UnloadApprovalWindow : Window
{
    private readonly OllamaSessions sessions;
    private readonly UnloadPlan plan;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal UnloadGrant? Grant { get; private set; }
    internal UnloadApprovalWindow(OllamaSessions sessions, UnloadPlan plan)
    {
        this.sessions = sessions; this.plan = plan; InitializeComponent();
        TargetText.Text = plan.Model.Name + "\nSHA-256: " + plan.Model.Digest + "\n서버: " + OllamaSessions.Endpoint + " · " + plan.Version;
        timer.Tick += (_, _) => UpdateConfirmation(); Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop(); UpdateConfirmation();
    }
    internal void UpdateConfirmation()
    {
        try { sessions.Validate(plan); ConfirmButton.IsEnabled = Acknowledged.IsChecked == true; ExpiryText.Text = "30초 안에 승인하세요. 만료 시 새 조회와 확인이 필요합니다."; }
        catch (ModelSessionException) { ConfirmButton.IsEnabled = false; ExpiryText.Text = "계획이 만료되었거나 세션이 닫혔습니다."; }
    }
    private void Changed(object sender, RoutedEventArgs e) { if (IsInitialized) UpdateConfirmation(); }
    private void Confirm(object sender, RoutedEventArgs e)
    {
        UpdateConfirmation(); if (!ConfirmButton.IsEnabled) return;
        try { Grant = sessions.ConfirmFromDesktop(plan); DialogResult = true; } catch (ModelSessionException) { UpdateConfirmation(); }
    }
}
