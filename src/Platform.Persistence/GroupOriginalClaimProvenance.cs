using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Original immutable acquisition authority at the chunk's commit time. An
// expired historical claim can prove its earlier commit; it grants no current
// authority and does not prove the complete business-effect ledger.
internal sealed class GroupOriginalClaimProvenance
{
    private GroupOriginalClaimProvenance(GroupScope scope, Guid batch, Guid workOperation, Guid claimOperation, Guid owner,
        long epoch, string authority, DateTimeOffset issued, DateTimeOffset expires, DateTimeOffset committed)
    {
        Scope = scope; BatchId = batch; WorkOperationId = workOperation; ClaimOperationId = claimOperation; OwnerId = owner;
        Epoch = epoch; AuthoritySha256 = authority; IssuedAtUtc = issued; ExpiresAtUtc = expires; CommittedAtUtc = committed;
    }
    internal GroupScope Scope { get; }
    internal Guid BatchId { get; }
    internal Guid WorkOperationId { get; }
    internal Guid ClaimOperationId { get; }
    internal Guid OwnerId { get; }
    internal long Epoch { get; }
    internal string AuthoritySha256 { get; }
    internal DateTimeOffset IssuedAtUtc { get; }
    internal DateTimeOffset ExpiresAtUtc { get; }
    internal DateTimeOffset CommittedAtUtc { get; }
    public override string ToString() => "Group original claim provenance (private metadata).";

    internal static GroupOriginalClaimProvenance Require(GroupWorkCommitReceiptRecord work, GroupBatchClaimReceiptRecord original)
    {
        ArgumentNullException.ThrowIfNull(work); ArgumentNullException.ThrowIfNull(original);
        if (work.DependencyManifestVersion != GroupWorkDependencyManifest.Version) throw Unavailable();
        var manifest = GroupWorkDependencyManifest.Read(work.DependencyManifest);
        var scope = manifest.Scope;
        if (work.TenantId != scope.TenantId || work.CompanyId != scope.CompanyId || work.BindingId != scope.SourceBindingId
            || work.BatchId != manifest.BatchId || work.OperationId != manifest.OperationId
            || work.SelectedMessageCount != manifest.Sources.Count || work.NoteCount is < 0 or > GroupAutomaticNotePlan.MaximumNotes
            || !Enum.IsDefined(work.Outcome) || (work.NoteCount == 0) != (work.Outcome == GroupWorkCommitOutcome.NoWork)
            || original.TenantId != scope.TenantId || original.CompanyId != scope.CompanyId || original.BindingId != scope.SourceBindingId
            || original.BatchId != work.BatchId || original.Epoch != work.ClaimEpoch || original.Epoch <= 0
            || original.OwnerId == Guid.Empty || original.OperationId == Guid.Empty || original.ServiceId == Guid.Empty
            || original.ServiceId != work.ServiceId || original.CredentialEpoch <= 0 || original.CredentialEpoch != work.CredentialEpoch
            || original.GrantVersion <= 0 || original.GrantVersion != work.GrantVersion
            || original.SourceVersion <= 0 || original.SourceVersion != work.SourceVersion
            || original.DeletionGeneration < 0 || original.DeletionGeneration != work.DeletionGeneration
            || original.AccountVersion <= 0 || original.AccountVersion != work.AccountVersion
            || !string.Equals(original.AuthoritySha256, manifest.AuthoritySha256, StringComparison.Ordinal)
            || original.RequestedLifetimeTicks < GroupBatchClaimStore.MinimumLifetime.Ticks
            || original.RequestedLifetimeTicks > GroupBatchClaimStore.MaximumLifetime.Ticks
            || original.IssuedAtUtc.Offset != TimeSpan.Zero || original.ExpiresAtUtc.Offset != TimeSpan.Zero
            || (original.ExpiresAtUtc - original.IssuedAtUtc).Ticks != original.RequestedLifetimeTicks
            || work.CommittedAtUtc.Offset != TimeSpan.Zero || work.CommittedAtUtc < original.IssuedAtUtc
            || work.CommittedAtUtc >= original.ExpiresAtUtc) throw Unavailable();
        return new(scope, work.BatchId, work.OperationId, original.OperationId, original.OwnerId, original.Epoch,
            original.AuthoritySha256, original.IssuedAtUtc, original.ExpiresAtUtc, work.CommittedAtUtc);
    }
    private static InvalidOperationException Unavailable() => new("Original group claim provenance is not available.");
}
