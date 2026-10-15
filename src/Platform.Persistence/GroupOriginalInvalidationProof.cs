using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Shape/digest authentication only. These helpers cannot authorize a SQL write,
// prove that a mismatch was a benign edit, or release a replacement generation.
internal static class GroupOriginalInvalidationProof
{
    internal static string Digest(GroupOriginalInvalidationRecord row) => GroupWorkDependencyManifest.Fingerprint(
        "aioffice-group-original-invalidation-checkpoint-v1", new
        {
            row.TenantId,
            row.CompanyId,
            row.BindingId,
            row.BatchId,
            row.FormatVersion,
            row.PlanSha256,
            row.OriginalClaimOperationId,
            row.OriginalClaimEpoch,
            row.ClaimOperationId,
            row.ClaimEpoch,
            row.ObservedDependenciesSha256,
            row.CreatedAtUtc
        });

    internal static void Require(GroupOriginalInvalidationRecord row, GroupScope scope, Guid batch,
        GroupAuthenticatedOriginalMetadata original, GroupBatchClaimReceiptRecord checkpointClaim, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row); ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(checkpointClaim); scope.Validate();
        if (batch == Guid.Empty || row.TenantId != scope.TenantId || row.CompanyId != scope.CompanyId
            || row.BindingId != scope.SourceBindingId || row.BatchId != batch || row.FormatVersion != 1
            || original.Plan.Dependencies.Scope != scope || original.Plan.Dependencies.BatchId != batch
            || row.PlanSha256 != original.Plan.Sha256 || row.OriginalClaimOperationId != original.OriginalClaimOperationId
            || row.OriginalClaimEpoch != original.OriginalClaimEpoch || row.OriginalClaimOperationId == Guid.Empty
            || row.OriginalClaimEpoch <= 0 || row.ClaimOperationId == Guid.Empty || row.ClaimEpoch < row.OriginalClaimEpoch
            || (row.ClaimEpoch == row.OriginalClaimEpoch && row.ClaimOperationId != row.OriginalClaimOperationId)
            || checkpointClaim.TenantId != scope.TenantId || checkpointClaim.CompanyId != scope.CompanyId
            || checkpointClaim.BindingId != scope.SourceBindingId || checkpointClaim.BatchId != batch
            || checkpointClaim.OperationId != row.ClaimOperationId || checkpointClaim.Epoch != row.ClaimEpoch
            || checkpointClaim.AuthoritySha256 != original.Plan.Dependencies.AuthoritySha256
            || checkpointClaim.OwnerId == Guid.Empty || checkpointClaim.ServiceId == Guid.Empty || checkpointClaim.CredentialEpoch <= 0
            || checkpointClaim.GrantVersion <= 0 || checkpointClaim.SourceVersion <= 0 || checkpointClaim.DeletionGeneration < 0
            || checkpointClaim.AccountVersion <= 0 || checkpointClaim.RequestedLifetimeTicks < GroupBatchClaimStore.MinimumLifetime.Ticks
            || checkpointClaim.RequestedLifetimeTicks > GroupBatchClaimStore.MaximumLifetime.Ticks
            || (checkpointClaim.ExpiresAtUtc - checkpointClaim.IssuedAtUtc).Ticks != checkpointClaim.RequestedLifetimeTicks
            || now.Offset != TimeSpan.Zero || original.CreatedAtUtc.Offset != TimeSpan.Zero
            || row.CreatedAtUtc.Offset != TimeSpan.Zero || row.CreatedAtUtc < original.CreatedAtUtc || row.CreatedAtUtc > now
            || checkpointClaim.IssuedAtUtc.Offset != TimeSpan.Zero || checkpointClaim.ExpiresAtUtc.Offset != TimeSpan.Zero
            || row.CreatedAtUtc < checkpointClaim.IssuedAtUtc || row.CreatedAtUtc >= checkpointClaim.ExpiresAtUtc
            || !Hash(row.PlanSha256) || !Hash(row.ObservedDependenciesSha256) || !Hash(row.CheckpointSha256)
            || Digest(row) != row.CheckpointSha256) throw Unavailable();
    }

    private static bool Hash(string? value) => value is { Length: 64 } && !value.AsSpan().ContainsAnyExcept("0123456789ABCDEF");
    private static InvalidOperationException Unavailable() => new("Group original invalidation proof is unavailable.");
}
