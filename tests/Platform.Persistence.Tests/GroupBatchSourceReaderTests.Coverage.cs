using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("source-add")]
    [InlineData("account-add")]
    [InlineData("source-reconnect")]
    [InlineData("source-reason")]
    [InlineData("account-reason")]
    [InlineData("source-remove")]
    [InlineData("account-remove")]
    [InlineData("source-time")]
    [InlineData("account-time")]
    public async Task AlreadyIncompleteCoverageCannotHideAnyExactDependencyChange(string change)
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var source = SourceGap(f); var account = AccountGap(f);
        f.Auth.Db.AddRange(source, account); await f.Auth.Db.SaveChangesAsync();
        var context = await f.Reader.ReadAsync(claim, [message.MessageId]);
        Assert.True(context.HasCoverageGap); await f.Reader.RequireCurrentAsync(context);
        switch (change)
        {
            case "source-add": f.Auth.Db.Add(SourceGap(f)); break;
            case "account-add": f.Auth.Db.Add(AccountGap(f, 2)); break;
            case "source-reconnect": source.ReconnectedAtUtc = source.OpenedAtUtc.AddTicks(1); break;
            case "source-reason": source.Reason = "another-gap"; break;
            case "account-reason":
                f.Auth.Db.Remove(account); f.Auth.Db.Add(AccountGap(f, reason: "listener-stopped")); break;
            case "source-remove": f.Auth.Db.Remove(source); break;
            case "account-remove": f.Auth.Db.Remove(account); break;
            case "source-time": source.OpenedAtUtc -= TimeSpan.FromTicks(1); break;
            case "account-time": account.RecordedAtUtc += TimeSpan.FromTicks(1); break;
        }
        await f.Auth.Db.SaveChangesAsync(); var before = await f.CountsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.RequireCurrentAsync(context));
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(1, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
        Assert.True((await f.Reader.ReadAsync(claim, [message.MessageId])).HasCoverageGap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GapAddedDuringKeyAwaitRefusesAlreadyIncompleteContextAndDisposesMaterial(bool account)
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        f.Auth.Db.AddRange(SourceGap(f), AccountGap(f)); await f.Auth.Db.SaveChangesAsync();
        f.Keys.BeforeRead = async () =>
        {
            if (account) f.Auth.Db.Add(AccountGap(f, 2)); else f.Auth.Db.Add(SourceGap(f));
            await f.Auth.Db.SaveChangesAsync();
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, [message.MessageId]));
        Assert.Equal(1, f.Keys.Reads);
        Assert.Throws<ObjectDisposedException>(() => f.Keys.LastMaterial!.Key.Length);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, 256)]
    [InlineData(false, 257)]
    [InlineData(true, 256)]
    [InlineData(true, 257)]
    public async Task CoverageSelectionRetainsExactMaximumAndRefusesOverflowBeforeKeys(bool account, int count)
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        for (var index = 0; index < count; index++)
        {
            if (account) f.Auth.Db.Add(AccountGap(f, index + 1)); else f.Auth.Db.Add(SourceGap(f));
        }
        await f.Auth.Db.SaveChangesAsync(); var before = await f.CountsAsync();
        if (count == GroupBatchCoverageSnapshot.MaximumRecordsPerKind)
        {
            var context = await f.Reader.ReadAsync(claim, [message.MessageId]);
            Assert.True(context.HasCoverageGap); await f.Reader.RequireCurrentAsync(context);
            Assert.Equal(1, f.Keys.Reads);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, [message.MessageId]));
            Assert.Equal(0, f.Keys.Reads);
        }
        Assert.Equal(before, await f.CountsAsync()); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignGroupOrCompanyCoverageNeverEntersExactContextDependencies(bool account)
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var context = await f.Reader.ReadAsync(claim, [message.MessageId]);
        if (account)
        {
            var row = AccountGap(f); row.CompanyId = Guid.NewGuid(); f.Auth.Db.Add(row);
        }
        else
        {
            var row = SourceGap(f); row.BindingId = Guid.NewGuid(); f.Auth.Db.Add(row);
        }
        await f.Auth.Db.SaveChangesAsync(); await f.Reader.RequireCurrentAsync(context);
        Assert.False((await f.Reader.ReadAsync(claim, [message.MessageId])).HasCoverageGap);
        Assert.Equal(2, f.Keys.Reads);
    }

    [Theory]
    [InlineData("source-empty-id")]
    [InlineData("source-sequence")]
    [InlineData("source-reason")]
    [InlineData("source-timezone")]
    [InlineData("source-reconnect")]
    [InlineData("account-epoch")]
    [InlineData("account-reason")]
    [InlineData("account-timezone")]
    [InlineData("account-time")]
    public async Task MalformedCoverageMetadataRefusesBeforePrivateKeyLookup(string corruption)
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var source = SourceGap(f); var account = AccountGap(f);
        switch (corruption)
        {
            case "source-empty-id": source.Id = Guid.Empty; break;
            case "source-sequence": source.AfterCommittedSequence = -1; break;
            case "source-reason": source.Reason = "private\nreason"; break;
            case "source-timezone": source.OpenedAtUtc = source.OpenedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "source-reconnect": source.ReconnectedAtUtc = source.OpenedAtUtc.AddTicks(-1); break;
            case "account-epoch": account.ListenerEpoch = 0; break;
            case "account-reason": account.Reason = "unknown"; break;
            case "account-timezone": account.RecordedAtUtc = account.RecordedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "account-time": account.RecordedAtUtc = account.OpenedAtUtc.AddTicks(-1); break;
        }
        f.Auth.Db.AddRange(source, account); await f.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, [message.MessageId]));
        Assert.Equal(0, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    private static GroupCoverageGapRecord SourceGap(Fixture f) => new()
    {
        TenantId = f.Auth.Scope.TenantId,
        CompanyId = f.Auth.Scope.CompanyId,
        BindingId = f.Auth.Scope.SourceBindingId,
        Id = Guid.NewGuid(),
        AfterCommittedSequence = 1,
        Reason = "owned-gap",
        OpenedAtUtc = f.Auth.Clock.Current
    };

    private static GroupAccountCoverageGapRecord AccountGap(Fixture f, long epoch = 1, string reason = "listener-expired") => new()
    {
        TenantId = f.Auth.Scope.TenantId,
        CompanyId = f.Auth.Scope.CompanyId,
        ConnectorAccountId = f.Auth.Account.Id,
        ListenerEpoch = epoch,
        Reason = reason,
        OpenedAtUtc = f.Auth.Clock.Current,
        RecordedAtUtc = f.Auth.Clock.Current
    };
}
