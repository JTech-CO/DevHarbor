using System.Diagnostics;
using System.Security.Principal;

namespace DevHarbor.Execution;

// Opaque, in-process capability. No bool, JSON approval, IPC grant, or public constructor.
internal sealed class ApprovalGrant(string id) { internal string Id { get; } = id; }
internal sealed class ApprovalTicket(CleanupPlan plan, DateTimeOffset expires)
{
    internal CleanupPlan Plan { get; } = plan;
    internal DateTimeOffset Expires { get; } = expires;
}
internal sealed class ApprovalBroker(TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, (ApprovalGrant Grant, string Plan, string Digest, DateTimeOffset Expires)> grants = [];
    private bool disposed;
    internal string Session { get; } = Guid.NewGuid().ToString("N");
    internal string UserSid { get; } = WindowsIdentity.GetCurrent().User!.Value;
    internal int SessionId { get; } = Process.GetCurrentProcess().SessionId;
    internal DateTimeOffset Now => time.GetUtcNow();
    internal void CheckPlan(CleanupPlan plan)
    {
        if (disposed || plan.Data.BrokerSession != Session || plan.Data.UserSid != UserSid || plan.Data.SessionId != SessionId
            || WindowsIdentity.GetCurrent().User!.Value != UserSid || Process.GetCurrentProcess().SessionId != SessionId)
            throw new WorkflowException("SessionMismatch");
        if (Now < plan.Data.CreatedAt || Now >= plan.Data.ExpiresAt) throw new WorkflowException("Expired");
    }
    // Called only by the trusted desktop confirmation window, or the dedicated test assembly.
    internal ApprovalGrant IssueFromLocalConfirmation(CleanupPlan plan)
    {
        lock (gate)
        {
            CheckPlan(plan);
            var grant = new ApprovalGrant(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            var expires = Now.AddSeconds(30); if (expires > plan.Data.ExpiresAt) expires = plan.Data.ExpiresAt;
            grants.Add(grant.Id, (grant, plan.Id, plan.Digest, expires)); return grant;
        }
    }
    internal ApprovalTicket Consume(CleanupPlan plan, ApprovalGrant grant)
    {
        lock (gate)
        {
            CheckPlan(plan);
            if (!grants.Remove(grant.Id, out var stored) || !ReferenceEquals(stored.Grant, grant)
                || stored.Plan != plan.Id || stored.Digest != plan.Digest) throw new WorkflowException("InvalidOrUsedApproval");
            if (Now >= stored.Expires) throw new WorkflowException("Expired");
            return new(plan, stored.Expires);
        }
    }
    internal void CheckTicket(ApprovalTicket ticket)
    {
        lock (gate) { CheckPlan(ticket.Plan); if (Now >= ticket.Expires) throw new WorkflowException("Expired"); }
    }
    public void Dispose() { lock (gate) { disposed = true; grants.Clear(); } }
}
