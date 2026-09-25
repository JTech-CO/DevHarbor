using DevHarbor.Domain;

var now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
var evidence = new RecoveryEvidence("fixture vendor contract", "artifact-digest", now.AddMinutes(-1), now.AddMinutes(10), true);
var item = new Artifact("item-1", "fixture-tool", 1024, RecoveryClass.Regenerable, UsageState.Idle, StoreEnvironment.NativeLocal, false, evidence);
var capability = new AdapterCapability(true, true, true, RecoveryMethod.Regenerate);
var failures = new List<string>();
var total = 0;

void Check(string name, Action check)
{
    total++;
    try { check(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures.Add(name); Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Reject(Artifact candidate, ExclusionReason reason, AdapterCapability? adapter = null)
{
    var result = ArtifactPolicy.EvaluateCandidate(candidate, adapter ?? capability, now);
    Require(!result.IsCandidate && result.Reasons.Contains(reason), $"Expected {reason}");
}

Check("Verified fixture is a candidate, never an execution permission", () =>
{
    Require(ArtifactPolicy.EvaluateCandidate(item, capability, now).IsCandidate, "Positive control failed");
    Require(!ProductCapabilities.CleanupEnabled && !ProductCapabilities.ApprovalBrokerEnabled, "P0 enabled mutations");
});
Check("User checkpoints remain excluded even with regeneration evidence", () => Reject(item with { IsUserAsset = true }, ExclusionReason.UserAsset));
Check("Protected stores cannot become eligible through verified evidence", () => Reject(item with { Recovery = RecoveryClass.Protected }, ExclusionReason.Protected));
Check("Missing and unverified evidence cannot prove recoverability", () =>
{
    Reject(item with { Evidence = null }, ExclusionReason.MissingEvidence);
    Reject(item with { Evidence = evidence with { Verified = false } }, ExclusionReason.UnverifiedEvidence);
});
Check("Expiry boundary and future evidence fail closed", () =>
{
    Reject(item with { Evidence = evidence with { ValidUntil = now } }, ExclusionReason.ExpiredEvidence);
    Reject(item with { Evidence = evidence with { ObservedAt = now.AddMinutes(1) } }, ExclusionReason.InvalidEvidence);
});
Check("Busy or unknown use cannot be cleaned", () =>
{
    Reject(item with { Usage = UsageState.Busy }, ExclusionReason.Busy);
    Reject(item with { Usage = UsageState.Unknown }, ExclusionReason.UnknownUsage);
});
Check("Remote Docker and WSL are not native cleanup targets", () =>
{
    Reject(item with { Environment = StoreEnvironment.Remote }, ExclusionReason.UnsupportedEnvironment);
    Reject(item with { Environment = StoreEnvironment.Wsl }, ExclusionReason.UnsupportedEnvironment);
});
Check("Unknown size is not zero; measured empty item stays distinguishable", () =>
{
    Reject(item with { LogicalBytes = null }, ExclusionReason.UnknownSize);
    Reject(item with { LogicalBytes = -1 }, ExclusionReason.InvalidSize);
    Require(ArtifactPolicy.EvaluateCandidate(item with { LogicalBytes = 0 }, capability, now).IsCandidate, "Zero was treated as unknown");
});
Check("Unknown adapter version or format disables eligibility", () =>
{
    Reject(item, ExclusionReason.UnsupportedVersion, capability with { VersionVerified = false });
    Reject(item, ExclusionReason.UnsupportedLayout, capability with { LayoutVerified = false });
});
Check("Read-only adapter and missing recovery cannot enable cleanup", () =>
{
    Reject(item, ExclusionReason.CleanupNotSupported, capability with { SupportsScopedCleanup = false });
    Reject(item, ExclusionReason.NoRecoveryMethod, capability with { RecoveryMethod = RecoveryMethod.None });
});
Check("Invalid serialized enum values fail closed", () =>
{
    Reject(item with { Recovery = (RecoveryClass)999 }, ExclusionReason.UnprovenRecovery);
    Reject(item with { Usage = (UsageState)999 }, ExclusionReason.UnknownUsage);
    Reject(item, ExclusionReason.NoRecoveryMethod, capability with { RecoveryMethod = (RecoveryMethod)999 });
});
Check("All independent exclusion reasons remain visible", () =>
{
    var result = ArtifactPolicy.EvaluateCandidate(item with { IsUserAsset = true, Usage = UsageState.Busy, Evidence = null }, capability, now);
    Require(result.Reasons.Contains(ExclusionReason.UserAsset) && result.Reasons.Contains(ExclusionReason.Busy) && result.Reasons.Contains(ExclusionReason.MissingEvidence), "Reasons lost");
});
Console.WriteLine($"{total - failures.Count}/{total} contract checks passed.");
return failures.Count == 0 ? 0 : 1;
