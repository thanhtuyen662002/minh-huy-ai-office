using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class GroupListenerLeaseTests
{
    private static readonly GroupListenerAccountScope Account = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
    private static GroupListenerLeaseSnapshot Current => new(Account, Owner, 7, Now, Now.AddSeconds(30));

    [Fact]
    public void FirstAcquireAndLostAckKeepOneOwnerWithoutExtendingDeadline()
    {
        var command = new GroupListenerCommand(Owner, GroupListenerOperation.Acquire, 0);
        var first = GroupListenerLeasePolicy.Apply(Account, command, null, Now);
        Assert.Equal(1, first.Lease.Epoch); Assert.Equal(Now.AddSeconds(30), first.Lease.ExpiresAtUtc);
        Assert.Equal("listener-started", first.CoverageReason); Assert.Equal(Now, first.CoverageOpenedAtUtc);
        var retry = GroupListenerLeasePolicy.Apply(Account, command, first.Lease, Now.AddSeconds(29));
        Assert.False(retry.Changed); Assert.Equal(first.Lease, retry.Lease);
        Assert.Null(retry.CoverageReason); Assert.Null(retry.CoverageOpenedAtUtc);
    }

    [Theory]
    [InlineData(GroupListenerOperation.Acquire, 0)]
    [InlineData(GroupListenerOperation.Renew, 7)]
    [InlineData(GroupListenerOperation.Stop, 7)]
    public void AnotherOwnerCannotTakeUnexpiredAccount(GroupListenerOperation operation, long epoch)
    {
        Assert.Throws<UnauthorizedAccessException>(() => GroupListenerLeasePolicy.Apply(Account,
            new(Guid.NewGuid(), operation, epoch), Current, Now.AddSeconds(29)));
    }

    [Theory]
    [InlineData(GroupListenerOperation.Renew)]
    [InlineData(GroupListenerOperation.Stop)]
    public void OldEpochCannotRenewOrStopNewOwnership(GroupListenerOperation operation)
    {
        Assert.Throws<UnauthorizedAccessException>(() => GroupListenerLeasePolicy.Apply(Account,
            new(Owner, operation, 6), Current, Now.AddSeconds(1)));
    }

    [Fact]
    public void ExactExpiryAllowsTakeoverAndRetainsMonotonicFenceAndUnknownCoverage()
    {
        var nextOwner = Guid.NewGuid();
        var next = GroupListenerLeasePolicy.Apply(Account, new(nextOwner, GroupListenerOperation.Acquire, 0), Current, Now.AddSeconds(30));
        Assert.Equal(nextOwner, next.Lease.OwnerId); Assert.Equal(8, next.Lease.Epoch);
        Assert.Equal("listener-expired", next.CoverageReason); Assert.Equal(Now, next.CoverageOpenedAtUtc);
        Assert.Throws<UnauthorizedAccessException>(() => GroupListenerLeasePolicy.Apply(Account,
            new(Owner, GroupListenerOperation.Renew, 7), next.Lease, Now.AddSeconds(31)));
    }

    [Fact]
    public void RenewalRetainsEpochAndStopExpiresWithoutDeletingFence()
    {
        var renewed = GroupListenerLeasePolicy.Apply(Account, new(Owner, GroupListenerOperation.Renew, 7), Current, Now.AddSeconds(10));
        Assert.Equal(7, renewed.Lease.Epoch); Assert.Equal(Now.AddSeconds(40), renewed.Lease.ExpiresAtUtc);
        Assert.Null(renewed.CoverageReason);
        var stopped = GroupListenerLeasePolicy.Apply(Account, new(Owner, GroupListenerOperation.Stop, 7), renewed.Lease, Now.AddSeconds(11));
        Assert.Equal(Now.AddSeconds(11), stopped.Lease.ExpiresAtUtc); Assert.Equal(7, stopped.Lease.Epoch);
        Assert.Equal("listener-stopped", stopped.CoverageReason);
        var repeatedStop = GroupListenerLeasePolicy.Apply(Account, new(Owner, GroupListenerOperation.Stop, 7), stopped.Lease, Now.AddSeconds(12));
        Assert.False(repeatedStop.Changed); Assert.Equal(stopped.Lease, repeatedStop.Lease); Assert.Null(repeatedStop.CoverageReason);
        var restarted = GroupListenerLeasePolicy.Apply(Account, new(Guid.NewGuid(), GroupListenerOperation.Acquire, 0), stopped.Lease, Now.AddSeconds(11));
        Assert.Equal(8, restarted.Lease.Epoch);
    }

    [Theory]
    [InlineData(GroupListenerOperation.Renew)]
    [InlineData(GroupListenerOperation.Stop)]
    public void MissingOrExactlyExpiredLeaseRefusesRenewAndStop(GroupListenerOperation operation)
    {
        var command = new GroupListenerCommand(Owner, operation, 7);
        Assert.Throws<UnauthorizedAccessException>(() => GroupListenerLeasePolicy.Apply(Account, command, null, Now));
        Assert.Throws<UnauthorizedAccessException>(() => GroupListenerLeasePolicy.Apply(Account, command, Current, Now.AddSeconds(30)));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("owner")]
    [InlineData("epoch")]
    [InlineData("future-heartbeat")]
    [InlineData("reversed-times")]
    [InlineData("long-lease")]
    [InlineData("offset")]
    public void MalformedPersistedLeaseCannotBeReinterpretedAsVacant(string change)
    {
        var current = change switch
        {
            "scope" => Current with { Account = Account with { ConnectorAccountId = Guid.NewGuid() } },
            "owner" => Current with { OwnerId = Guid.Empty },
            "epoch" => Current with { Epoch = 0 },
            "future-heartbeat" => Current with { HeartbeatAtUtc = Now.AddSeconds(1) },
            "reversed-times" => Current with { ExpiresAtUtc = Now.AddSeconds(-1) },
            "long-lease" => Current with { ExpiresAtUtc = Now.AddSeconds(31) },
            _ => Current with { HeartbeatAtUtc = Now.ToOffset(TimeSpan.FromHours(7)) }
        };
        Assert.Throws<InvalidOperationException>(() => GroupListenerLeasePolicy.Apply(Account,
            new(Owner, GroupListenerOperation.Acquire, 0), current, Now));
    }

    [Fact]
    public void EpochAndClockOverflowRefuseInsteadOfWrappingOwnership()
    {
        Assert.Throws<InvalidOperationException>(() => GroupListenerLeasePolicy.Apply(Account,
            new(Owner, GroupListenerOperation.Acquire, 0), Current with { Epoch = long.MaxValue }, Now.AddSeconds(30)));
        Assert.Throws<InvalidOperationException>(() => GroupListenerLeasePolicy.Apply(Account,
            new(Owner, GroupListenerOperation.Acquire, 0), null, DateTimeOffset.MaxValue));
    }

    [Theory]
    [InlineData(GroupListenerOperation.Acquire, 1)]
    [InlineData(GroupListenerOperation.Acquire, -1)]
    [InlineData(GroupListenerOperation.Renew, 0)]
    [InlineData(GroupListenerOperation.Stop, -1)]
    [InlineData((GroupListenerOperation)99, 0)]
    public void InvalidOperationAndEpochCombinationCannotChangeLease(GroupListenerOperation operation, long epoch)
    {
        Assert.Throws<InvalidOperationException>(() => GroupListenerLeasePolicy.Apply(Account, new(Owner, operation, epoch), Current, Now));
    }

    [Fact]
    public void EmptyOwnerScopeAndNonUtcClockAreRejected()
    {
        var command = new GroupListenerCommand(Owner, GroupListenerOperation.Acquire, 0);
        Assert.Throws<InvalidOperationException>(() => GroupListenerLeasePolicy.Apply(Account, command with { OwnerId = Guid.Empty }, null, Now));
        foreach (var empty in new[] { Account with { TenantId = Guid.Empty }, Account with { CompanyId = Guid.Empty }, Account with { ConnectorAccountId = Guid.Empty } })
            Assert.Throws<InvalidOperationException>(() => GroupListenerLeasePolicy.Apply(empty, command, null, Now));
        Assert.Throws<InvalidOperationException>(() => GroupListenerLeasePolicy.Apply(Account, command, null, Now.ToOffset(TimeSpan.FromHours(7))));
    }
}
