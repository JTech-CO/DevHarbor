namespace DevHarbor.Domain;

public enum RecoveryClass { Unknown, Regenerable, Protected }
public enum UsageState { Unknown, Idle, Busy }
public enum StoreEnvironment { NativeLocal, Wsl, Remote }
public enum RecoveryMethod { None, RecycleRestore, Redownload, Regenerate }

public sealed record RecoveryEvidence(
    string Source,
    string ArtifactIdentity,
    DateTimeOffset ObservedAt,
    DateTimeOffset ValidUntil,
    bool Verified);

// Size=null means unmeasured. Zero is a measured empty item.
public sealed record Artifact(
    string Id,
    string Owner,
    long? LogicalBytes,
    RecoveryClass Recovery,
    UsageState Usage,
    StoreEnvironment Environment,
    bool IsUserAsset,
    RecoveryEvidence? Evidence);

public sealed record AdapterCapability(
    bool LayoutVerified,
    bool VersionVerified,
    bool SupportsScopedCleanup,
    RecoveryMethod RecoveryMethod);

public enum ExclusionReason
{
    InvalidIdentity, InvalidSize, UnknownSize, Protected, UserAsset,
    UnprovenRecovery, UnsupportedEnvironment, Busy, UnknownUsage,
    MissingEvidence, UnverifiedEvidence, InvalidEvidence, ExpiredEvidence,
    UnsupportedLayout, UnsupportedVersion, CleanupNotSupported, NoRecoveryMethod
}

public sealed record CandidateDecision(IReadOnlyList<ExclusionReason> Reasons)
{
    // An eligibility result is NOT execution authorization.
    public bool IsCandidate => Reasons.Count == 0;
}

public static class ArtifactPolicy
{
    public static CandidateDecision EvaluateCandidate(Artifact item, AdapterCapability capability, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(capability);
        var reasons = new List<ExclusionReason>();
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Owner)) reasons.Add(ExclusionReason.InvalidIdentity);
        if (item.LogicalBytes is null) reasons.Add(ExclusionReason.UnknownSize);
        else if (item.LogicalBytes < 0) reasons.Add(ExclusionReason.InvalidSize);
        if (item.Recovery == RecoveryClass.Protected) reasons.Add(ExclusionReason.Protected);
        else if (item.Recovery != RecoveryClass.Regenerable) reasons.Add(ExclusionReason.UnprovenRecovery);
        if (item.IsUserAsset) reasons.Add(ExclusionReason.UserAsset);
        if (item.Environment != StoreEnvironment.NativeLocal) reasons.Add(ExclusionReason.UnsupportedEnvironment);
        if (item.Usage == UsageState.Busy) reasons.Add(ExclusionReason.Busy);
        else if (item.Usage != UsageState.Idle) reasons.Add(ExclusionReason.UnknownUsage);
        if (item.Evidence is not { } evidence) reasons.Add(ExclusionReason.MissingEvidence);
        else
        {
            if (!evidence.Verified) reasons.Add(ExclusionReason.UnverifiedEvidence);
            if (string.IsNullOrWhiteSpace(evidence.Source) || string.IsNullOrWhiteSpace(evidence.ArtifactIdentity)
                || evidence.ObservedAt > now || evidence.ValidUntil <= evidence.ObservedAt)
                reasons.Add(ExclusionReason.InvalidEvidence);
            if (evidence.ValidUntil <= now) reasons.Add(ExclusionReason.ExpiredEvidence);
        }
        if (!capability.LayoutVerified) reasons.Add(ExclusionReason.UnsupportedLayout);
        if (!capability.VersionVerified) reasons.Add(ExclusionReason.UnsupportedVersion);
        if (!capability.SupportsScopedCleanup) reasons.Add(ExclusionReason.CleanupNotSupported);
        if (!Enum.IsDefined(capability.RecoveryMethod) || capability.RecoveryMethod == RecoveryMethod.None)
            reasons.Add(ExclusionReason.NoRecoveryMethod);
        return new CandidateDecision(reasons.AsReadOnly());
    }
}

// P0 contains no mutation executor. No caller-supplied approval can enable it.
public static class ProductCapabilities
{
    public static bool CleanupEnabled => false;
    public static bool ApprovalBrokerEnabled => false;
    public static bool McpServerEnabled => false;
}
