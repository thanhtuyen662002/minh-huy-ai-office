using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("listener-started")]
    [InlineData("listener-expired")]
    [InlineData("foreign-account")]
    public async Task ActualListenerAccountGapPreservesReadableCandidateButDeniesNoWorkNativePreparation(string reason)
    {
        using var f = new Fixture();
        var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var baseline = await f.Reader.ReadAsync(claim, [message.MessageId]);
        Assert.False(baseline.HasCoverageGap);
        var account = new GroupListenerAccountScope(f.Auth.Scope.TenantId, f.Auth.Scope.CompanyId, f.Auth.Account.Id);
        var now = f.Auth.Clock.Current;
        var current = reason == "listener-expired"
            ? new GroupListenerLeaseSnapshot(account, Guid.NewGuid(), 1, now.AddSeconds(-30), now)
            : null;
        var transition = GroupListenerLeasePolicy.Apply(account,
            new(Guid.NewGuid(), GroupListenerOperation.Acquire, 0), current, now);
        Assert.Equal(reason == "listener-expired" ? "listener-expired" : "listener-started", transition.CoverageReason);
        f.Auth.Db.Add(new GroupAccountCoverageGapRecord
        {
            TenantId = account.TenantId,
            CompanyId = account.CompanyId,
            ConnectorAccountId = reason == "foreign-account" ? Guid.NewGuid() : account.ConnectorAccountId,
            ListenerEpoch = transition.Lease.Epoch,
            Reason = transition.CoverageReason!,
            OpenedAtUtc = transition.CoverageOpenedAtUtc!.Value,
            RecordedAtUtc = now
        });
        await f.Auth.Db.SaveChangesAsync();
        var source = await f.Reader.ReadAsync(claim, [message.MessageId]);
        var prepared = GroupBatchSourcePreparation.Create(source);
        Assert.Single(prepared.Candidates);
        Assert.All(prepared.Receipts, receipt => Assert.False(receipt.HasUnsupportedMedia));
        Assert.Equal(reason != "foreign-account", source.HasCoverageGap);
        // Exact frozen native preparation predicate, before the NoWork store.
        var nativeRejects = prepared.Candidates.Count != 1
            || prepared.Receipts.Any(receipt => receipt.HasUnsupportedMedia) || source.HasCoverageGap;
        Assert.Equal(reason != "foreign-account", nativeRejects);
    }
}
