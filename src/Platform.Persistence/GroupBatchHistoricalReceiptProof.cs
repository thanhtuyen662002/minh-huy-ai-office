using System.Security.Cryptography;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Proves the earlier terminal write against original immutable evidence and
// its actual acquisition window. An expired original lease remains evidence;
// this type grants no present-day authorization or permission to advance SQL.
internal static class GroupBatchHistoricalReceiptProof
{
    internal static void Require(GroupBatchHistoricalGraph historical, GroupBatchClaimReceiptRecord original,
        GroupBatchTerminalReceiptRecord receipt, Guid operation, DateTimeOffset now)
    {
        try
        {
            if (historical is null || original is null || receipt is null) throw Unavailable();
            var coverage = historical.Coverage; var scope = coverage.Scope;
            if (operation == Guid.Empty || now.Offset != TimeSpan.Zero || now < historical.ObservedAtUtc
                || original.TenantId != scope.TenantId || original.CompanyId != scope.CompanyId || original.BindingId != scope.SourceBindingId
                || original.BatchId != coverage.BatchId || original.OperationId == Guid.Empty || original.OwnerId == Guid.Empty
                || original.Epoch <= 0 || original.ServiceId == Guid.Empty || original.CredentialEpoch <= 0 || original.GrantVersion <= 0
                || original.SourceVersion <= 0 || original.DeletionGeneration < 0 || original.AccountVersion <= 0
                || original.AuthoritySha256 is not { Length: 64 }
                || original.AuthoritySha256.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
                || original.RequestedLifetimeTicks < GroupBatchClaimStore.MinimumLifetime.Ticks
                || original.RequestedLifetimeTicks > GroupBatchClaimStore.MaximumLifetime.Ticks
                || original.IssuedAtUtc.Offset != TimeSpan.Zero || original.ExpiresAtUtc.Offset != TimeSpan.Zero
                || original.ExpiresAtUtc != original.IssuedAtUtc.AddTicks(original.RequestedLifetimeTicks)
                || receipt.TenantId != scope.TenantId || receipt.CompanyId != scope.CompanyId || receipt.BindingId != scope.SourceBindingId
                || receipt.BatchId != coverage.BatchId || receipt.OperationId != operation
                || receipt.ManifestVersion != GroupBatchTerminalManifest.Version || receipt.ManifestSha256 is not { Length: 32 }
                || receipt.ClaimOperationId != original.OperationId || receipt.ClaimOwnerId != original.OwnerId || receipt.ClaimEpoch != original.Epoch
                || receipt.ServiceId != original.ServiceId || receipt.CredentialEpoch != original.CredentialEpoch
                || receipt.GrantVersion != original.GrantVersion || receipt.SourceVersion != original.SourceVersion
                || receipt.DeletionGeneration != original.DeletionGeneration || receipt.AccountVersion != original.AccountVersion
                || receipt.CommittedAtUtc.Offset != TimeSpan.Zero || receipt.CommittedAtUtc < original.IssuedAtUtc
                || receipt.CommittedAtUtc >= original.ExpiresAtUtc || receipt.CommittedAtUtc > historical.ObservedAtUtc
                || receipt.CommittedAtUtc < coverage.AllocatedAtUtc) throw Unavailable();
            foreach (var effect in historical.OriginalEffects)
            {
                var claim = effect.OriginalClaim;
                if (claim.Epoch > original.Epoch || claim.AuthoritySha256 != original.AuthoritySha256
                    || !effect.MatchesAcquisitionAuthority(original) || effect.CommittedAtUtc > receipt.CommittedAtUtc) throw Unavailable();
                if (claim.Epoch == original.Epoch && (claim.ClaimOperationId != original.OperationId || claim.OwnerId != original.OwnerId
                    || claim.IssuedAtUtc != original.IssuedAtUtc || claim.ExpiresAtUtc != original.ExpiresAtUtc)) throw Unavailable();
            }
            var stored = GroupBatchTerminalManifest.Read(receipt.Manifest);
            if (receipt.AfterSequence != stored.AfterSequence || receipt.ThroughSequence != stored.ThroughSequence
                || receipt.RawRevisionCount != stored.RawCount || receipt.SelectedMessageCount != stored.SelectedCount
                || receipt.ContributorCount != stored.Contributors.Count || receipt.NoteCount != stored.NoteCount
                || !CryptographicOperations.FixedTimeEquals(receipt.ManifestSha256, Convert.FromHexString(stored.Fingerprint))) throw Unavailable();
            GroupBatchTerminalManifest.RequireOriginalUnchanged(historical, operation, receipt.Manifest);
        }
        catch (Exception) { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Group historical terminal receipt proof is not available.");
}
