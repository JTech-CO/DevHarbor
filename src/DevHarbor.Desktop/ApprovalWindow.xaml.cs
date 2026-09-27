using System.Windows;
using System.Windows.Threading;
using DevHarbor.Execution;
namespace DevHarbor.Desktop;
public partial class ApprovalWindow : Window
{
    private readonly CleanupPlan plan;
    private readonly ApprovalBroker broker;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal ApprovalGrant? Grant { get; private set; }
    internal ApprovalWindow(CleanupPlan plan, ApprovalBroker broker)
    {
        this.plan = plan; this.broker = broker; InitializeComponent();
        bool restore = plan.Data.Action == WorkflowAction.RestoreSample;
        Heading.Text = restore ? "샘플 파일 복원 확인" : "샘플 파일 보관 확인";
        ConfirmButton.Content = restore ? "이 파일 복원 승인" : "이 파일 이동 승인";
        SourceText.Text = plan.Source;
        DestinationText.Text = plan.Destination;
        SizeText.Text = $"파일 1개 · {plan.Data.Snapshot.Stamp.Length:N0} bytes · 예상 확보 공간 0 bytes";
        RecoveryText.Text = restore ? "같은 이름의 파일이 있으면 덮어쓰지 않습니다. 다른 이름으로 복원하려면 새 계획과 승인이 필요합니다." : "보관 후 이력에서 별도 승인으로 복원할 수 있습니다. 원본 위치가 사용 중이거나 파일이 달라지면 중단합니다.";
        IdentityText.Text = $"파일 SHA-256: {plan.Data.Snapshot.Stamp.Sha256}\n계획 SHA-256: {plan.Digest}\n계획 ID: {plan.Id}\n세션: {plan.Data.SessionId}";
        timer.Tick += (_, _) => UpdateConfirmation();
        Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop(); UpdateConfirmation();
    }
    internal void UpdateConfirmation()
    {
        try
        {
            broker.CheckPlan(plan);
            ExpiryText.Text = $"계획 만료까지 {Math.Max(0, (int)(plan.Data.ExpiresAt - broker.Now).TotalSeconds)}초 · 승인은 한 번만 사용됩니다.";
            ConfirmButton.IsEnabled = Acknowledged.IsChecked == true;
        }
        catch (WorkflowException) { ConfirmButton.IsEnabled = false; ExpiryText.Text = "계획이 만료되었거나 세션이 닫혔습니다. 취소 후 새 계획을 만드세요."; }
    }
    private void AcknowledgementChanged(object sender, RoutedEventArgs e) { if (IsInitialized) UpdateConfirmation(); }
    private void Confirm(object sender, RoutedEventArgs e)
    {
        UpdateConfirmation(); if (!ConfirmButton.IsEnabled) return;
        try { Grant = broker.IssueFromLocalConfirmation(plan); DialogResult = true; }
        catch (WorkflowException) { UpdateConfirmation(); }
    }
}
