using System.Security.Cryptography;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Closed receipt/provenance prerequisite. Only the owned SQL consumer may use
// this after reconstructing current whole-batch dependencies under its fences.
internal static class GroupBatchTerminalCommitProof
{
    internal static GroupBatchTerminalReceiptRecord Stage(GroupWholeBatchDependencyVerdict.Current current,
        GroupBatchClaimReceipt lease, string authoritySha256, GroupBatchClaimReceiptRecord original,
        Guid operation, DateTimeOffset committed)
    {
        if (original is null || lease is null || ClaimReceipt(original) != lease) throw Unavailable();
        var manifest = GroupBatchTerminalManifest.Create(current, operation);
        var result = new GroupBatchTerminalReceiptRecord
        {
            TenantId = lease.Scope.TenantId,
            CompanyId = lease.Scope.CompanyId,
            BindingId = lease.Scope.SourceBindingId,
            BatchId = lease.BatchId,
            OperationId = operation,
            ManifestVersion = GroupBatchTerminalManifest.Version,
            Manifest = manifest.Write(),
            ManifestSha256 = Convert.FromHexString(manifest.Fingerprint),
            AfterSequence = manifest.AfterSequence,
            ThroughSequence = manifest.ThroughSequence,
            RawRevisionCount = manifest.RawCount,
            SelectedMessageCount = manifest.SelectedCount,
            ContributorCount = manifest.Contributors.Count,
            NoteCount = manifest.NoteCount,
            ClaimOperationId = original.OperationId,
            ClaimOwnerId = original.OwnerId,
            ClaimEpoch = original.Epoch,
            ServiceId = original.ServiceId,
            CredentialEpoch = original.CredentialEpoch,
            GrantVersion = original.GrantVersion,
            SourceVersion = original.SourceVersion,
            DeletionGeneration = original.DeletionGeneration,
            AccountVersion = original.AccountVersion,
            CommittedAtUtc = committed
        };
        Require(current, lease, authoritySha256, original, result, operation, committed);
        return result;
    }

    internal static void Require(GroupWholeBatchDependencyVerdict.Current current, GroupBatchClaimReceipt lease,
        string authoritySha256, GroupBatchClaimReceiptRecord original, GroupBatchTerminalReceiptRecord receipt,
        Guid operation, DateTimeOffset now)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(lease);
            ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(receipt);
            lease.Scope.Validate(); var coverage = current.Coverage ?? throw Unavailable(); var scope = lease.Scope;
            var effects = coverage.RequireOriginalEffects(current.OriginalEffects);
            if (operation == Guid.Empty || lease.OperationId == Guid.Empty || lease.OwnerId == Guid.Empty || lease.Epoch <= 0
                || coverage.Scope != scope || coverage.BatchId != lease.BatchId || now.Offset != TimeSpan.Zero
                || lease.IssuedAtUtc.Offset != TimeSpan.Zero || lease.ExpiresAtUtc.Offset != TimeSpan.Zero
                || lease.RequestedLifetimeTicks < GroupBatchClaimStore.MinimumLifetime.Ticks
                || lease.RequestedLifetimeTicks > GroupBatchClaimStore.MaximumLifetime.Ticks
                || lease.ExpiresAtUtc != lease.IssuedAtUtc.AddTicks(lease.RequestedLifetimeTicks)
                || now < lease.IssuedAtUtc || now >= lease.ExpiresAtUtc
                || authoritySha256 is not { Length: 64 } || authoritySha256.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
                || original.TenantId != scope.TenantId || original.CompanyId != scope.CompanyId || original.BindingId != scope.SourceBindingId
                || original.BatchId != lease.BatchId || original.OperationId == Guid.Empty || original.OwnerId == Guid.Empty
                || original.Epoch <= 0 || original.Epoch > lease.Epoch || original.AuthoritySha256 != authoritySha256
                || original.RequestedLifetimeTicks < GroupBatchClaimStore.MinimumLifetime.Ticks
                || original.RequestedLifetimeTicks > GroupBatchClaimStore.MaximumLifetime.Ticks
                || original.IssuedAtUtc.Offset != TimeSpan.Zero || original.ExpiresAtUtc.Offset != TimeSpan.Zero
                || original.ExpiresAtUtc != original.IssuedAtUtc.AddTicks(original.RequestedLifetimeTicks)
                || original.ServiceId != lease.ServiceId || original.ServiceId == Guid.Empty
                || original.CredentialEpoch != lease.CredentialEpoch || original.CredentialEpoch <= 0
                || original.GrantVersion != lease.GrantVersion || original.GrantVersion <= 0
                || original.SourceVersion != lease.SourceVersion || original.SourceVersion <= 0
                || original.DeletionGeneration != lease.DeletionGeneration || original.DeletionGeneration < 0
                || original.AccountVersion != lease.AccountVersion || original.AccountVersion <= 0
                || receipt.TenantId != scope.TenantId || receipt.CompanyId != scope.CompanyId || receipt.BindingId != scope.SourceBindingId
                || receipt.BatchId != coverage.BatchId || receipt.OperationId != operation
                || receipt.ManifestVersion != GroupBatchTerminalManifest.Version || receipt.ManifestSha256 is not { Length: 32 }
                || receipt.ClaimOperationId != original.OperationId || receipt.ClaimOwnerId != original.OwnerId || receipt.ClaimEpoch != original.Epoch
                || receipt.ServiceId != original.ServiceId || receipt.CredentialEpoch != original.CredentialEpoch
                || receipt.GrantVersion != original.GrantVersion || receipt.SourceVersion != original.SourceVersion
                || receipt.DeletionGeneration != original.DeletionGeneration || receipt.AccountVersion != original.AccountVersion
                || receipt.CommittedAtUtc.Offset != TimeSpan.Zero || receipt.CommittedAtUtc < original.IssuedAtUtc
                || receipt.CommittedAtUtc >= original.ExpiresAtUtc || receipt.CommittedAtUtc > now
                || receipt.CommittedAtUtc < coverage.AllocatedAtUtc || effects.Any(x => x.CommittedAtUtc > receipt.CommittedAtUtc)) throw Unavailable();
            var operations = new HashSet<Guid>(); var observed = 0;
            if (current.OriginalClaims is null) throw Unavailable();
            foreach (var claim in current.OriginalClaims)
                if (++observed > FrozenGroupBatch.MaximumMessages || claim is null || claim.Scope != scope || claim.BatchId != coverage.BatchId
                    || claim.Epoch > lease.Epoch || claim.AuthoritySha256 != authoritySha256 || !operations.Add(claim.WorkOperationId)
                    || !effects.Any(e => e.OperationId == claim.WorkOperationId && e.CommittedAtUtc == claim.CommittedAtUtc)) throw Unavailable();
            if (observed != effects.Count) throw Unavailable();
            var stored = GroupBatchTerminalManifest.Read(receipt.Manifest);
            if (receipt.AfterSequence != stored.AfterSequence || receipt.ThroughSequence != stored.ThroughSequence
                || receipt.RawRevisionCount != stored.RawCount || receipt.SelectedMessageCount != stored.SelectedCount
                || receipt.ContributorCount != stored.Contributors.Count || receipt.NoteCount != stored.NoteCount
                || !CryptographicOperations.FixedTimeEquals(receipt.ManifestSha256, Convert.FromHexString(stored.Fingerprint))) throw Unavailable();
            GroupBatchTerminalManifest.RequireUnchanged(current, operation, receipt.Manifest);
        }
        catch (Exception) { throw Unavailable(); }
    }

    internal static void RequireWritten(GroupWholeBatchDependencyVerdict.Current current, GroupBatchClaimReceipt lease,
        string authoritySha256, GroupBatchClaimReceiptRecord original, GroupBatchTerminalReceiptRecord observed,
        Guid operation, byte[] expectedManifest, DateTimeOffset expectedCommitUtc, DateTimeOffset now)
    {
        Require(current, lease, authoritySha256, original, observed, operation, now);
        if (expectedManifest is null || expectedManifest.Length is < GroupBatchTerminalManifest.MinimumBytes or > GroupBatchTerminalManifest.MaximumBytes
            || observed.CommittedAtUtc != expectedCommitUtc || !CryptographicOperations.FixedTimeEquals(observed.Manifest, expectedManifest)) throw Unavailable();
    }

    private static GroupBatchClaimReceipt ClaimReceipt(GroupBatchClaimReceiptRecord record) => new(
        new GroupScope(record.TenantId, record.CompanyId, record.BindingId), record.OperationId, record.BatchId,
        record.OwnerId, record.Epoch, record.RequestedLifetimeTicks, record.IssuedAtUtc, record.ExpiresAtUtc,
        record.ServiceId, record.CredentialEpoch, record.GrantVersion, record.SourceVersion, record.DeletionGeneration, record.AccountVersion);
    private static InvalidOperationException Unavailable() => new("Group terminal receipt proof is not available.");
}
