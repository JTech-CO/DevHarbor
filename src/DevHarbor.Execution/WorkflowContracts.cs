using System.Security.Cryptography;
using System.Text.Json;
using DevHarbor.Discovery;
using DevHarbor.Windows;

namespace DevHarbor.Execution;

// Cache measurements never grant cleanup capability.
public sealed record CleanupAssessment(string Tool, string? Root, bool CanExecute, IReadOnlyList<string> Reasons);
public static class CleanupEligibility
{
    public static CleanupAssessment Assess(StoreMeasurement item)
    {
        var reasons = new List<string>();
        if (item.Store.Tool is not (ToolKind.Pip or ToolKind.Uv)) reasons.Add("현재 제한 정리 대상 도구가 아닙니다.");
        if (item.Store.Status != DiscoveryStatus.Ready || item.Status != ScanStatus.Complete) reasons.Add("완전한 로컬 스캔 결과가 필요합니다.");
        if (item.Store.Environment != StorageEnvironment.NativeLocal) reasons.Add("로컬 Windows 저장소만 검토할 수 있습니다.");
        reasons.Add("사용 중인 파일·메모리 매핑과 도구별 삭제 범위 검증이 남아 있습니다.");
        reasons.Add("재생성 출처와 인증·네트워크 복구 가능성을 확인하지 않았습니다.");
        reasons.Add("실제 캐시 정리 기능이 비활성화되어 있습니다.");
        return new(item.Store.Tool.ToString(), item.Store.Root, false, reasons.AsReadOnly());
    }
}
internal enum WorkflowAction { HoldSample, RestoreSample }
internal sealed record PlanData(string Id, WorkflowAction Action, string UserSid, int SessionId,
    string BrokerSession, FileSnapshot Snapshot, string Store, QuarantineReceipt? Receipt, string? ParentId,
    string? RestoreLeaf, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
internal sealed class CleanupPlan(PlanData data)
{
    internal PlanData Data { get; } = data;
    internal string Payload { get; } = JsonSerializer.Serialize(data);
    internal string Digest => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Payload)));
    internal string Id => Data.Id;
    internal string Source => Data.Action == WorkflowAction.HoldSample ? Data.Snapshot.Path : Data.Receipt!.StoredPath;
    internal string Destination => Data.Action == WorkflowAction.HoldSample ? Data.Store : Path.Combine(Data.Snapshot.Root, Data.RestoreLeaf ?? "sample.txt");
}
internal sealed record WorkflowResult(string State, string? Reason = null, string? OperationId = null);
internal sealed record HistoryEntry(string Id, string Action, string State, string Source, string Destination, string? Reason, DateTimeOffset UpdatedAt);
internal sealed class WorkflowException(string reason) : IOException(reason) { internal string Reason { get; } = reason; }
