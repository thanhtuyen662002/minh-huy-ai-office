using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task ActualImmutableClaimReceiptAndActualSourceManifestProveEarlierCommitWithoutCurrentLeaseAuthority()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var handle = await f.ClaimAsync();
        var context = await f.Reader.ReadAsync(handle, [message.MessageId]);
        var original = await f.Auth.Db.GroupBatchClaimReceipts.AsNoTracking().SingleAsync(); var operation = Guid.NewGuid();
        // Claim and manifest are actual shipping InMemory state. The receipt
        // below is structural metadata, not a committed SQL business effect.
        var work = new GroupWorkCommitReceiptRecord
        {
            TenantId = context.Scope.TenantId,
            CompanyId = context.Scope.CompanyId,
            BindingId = context.Scope.SourceBindingId,
            BatchId = context.BatchId,
            OperationId = operation,
            SelectedMessageCount = 1,
            Outcome = GroupWorkCommitOutcome.NoWork,
            ServiceId = handle.Receipt.ServiceId,
            ClaimEpoch = handle.Receipt.Epoch,
            CredentialEpoch = handle.Receipt.CredentialEpoch,
            GrantVersion = handle.Receipt.GrantVersion,
            SourceVersion = handle.Receipt.SourceVersion,
            DeletionGeneration = handle.Receipt.DeletionGeneration,
            AccountVersion = handle.Receipt.AccountVersion,
            CommittedAtUtc = f.Auth.Clock.Current,
            DependencyManifestVersion = 1,
            DependencyManifest = GroupWorkDependencyManifest.Create(context, new(handle, [], []), operation)
        };
        f.Auth.Clock.Current = handle.Receipt.ExpiresAtUtc.AddTicks(1);
        var proven = GroupOriginalClaimProvenance.Require(work, original);
        Assert.Equal(original.AuthoritySha256, proven.AuthoritySha256); Assert.Equal(original.OperationId, proven.ClaimOperationId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GroupBatchClaimStore(f.Auth.Db, f.Worker, f.Auth.Clock).RequireCurrentAsync(handle));
    }
}
