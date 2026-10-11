using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeDependencyReaderMissingDependencyRefusesBeforeDisposedDatabase(bool missingBrain)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync(); f.Auth.Db.Dispose();
        var reader = new GroupWholeBatchDependencyReader(f.Auth.Db, f.Worker, f.Auth.Clock,
            missingBrain ? f.Reader : null!, missingBrain ? null! : BrainReader(f));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadLockedAsync(handle)); Assert.Equal(0, f.Keys.Reads);
    }

    [Theory]
    [InlineData(false, "database")]
    [InlineData(true, "database")]
    [InlineData(false, "worker")]
    [InlineData(true, "worker")]
    [InlineData(false, "clock")]
    [InlineData(true, "clock")]
    public async Task WholeDependencyReaderMismatchedDependenciesRefuseBeforeDisposedDatabase(bool changeBrain, string change)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        using var alternate = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var database = change == "database" ? alternate : f.Auth.Db;
        var worker = change == "worker" ? f.Worker with { ServiceId = Guid.NewGuid() } : f.Worker;
        var clock = change == "clock" ? TimeProvider.System : f.Auth.Clock;
        var source = changeBrain ? f.Reader : new GroupBatchSourceReader(database, worker, clock, f.Keys, new());
        var brain = changeBrain ? new GroupBrainCurrentReader(database, worker, clock, f.Keys, new()) : BrainReader(f);
        var reads = f.Keys.Reads; f.Auth.Db.Dispose();
        var reader = new GroupWholeBatchDependencyReader(f.Auth.Db, f.Worker, f.Auth.Clock, source, brain);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadLockedAsync(handle));
        Assert.Equal("Whole group batch dependencies are not available.", error.Message); Assert.Equal(reads, f.Keys.Reads);
    }

    [Theory]
    [InlineData("ReceiptRows", 101, "GroupWorkCommitReceipts")]
    [InlineData("SelectedRows", 101, "GroupWorkSourceDispositions")]
    [InlineData("RawRows", 501, "GroupWorkRawDispositions")]
    [InlineData("CutoffRows", 1, "GroupMessageRevisions")]
    [InlineData("OriginalClaimRows", 101, "GroupBatchClaimReceipts")]
    public void WholeDependencyReaderActualBoundedEfQueriesParseSql160WithoutConnecting(string method, int maximum, string table)
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1").Options);
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var worker = new GroupExtractionWorkerBinding(scope.TenantId, scope.CompanyId, Guid.NewGuid(), 1);
        var reader = new GroupWholeBatchDependencyReader(db, worker, TimeProvider.System, null!, null!);
        object[] parameters = method == "CutoffRows" ? [scope, Guid.NewGuid(), 500L]
            : method == "OriginalClaimRows" ? [scope, Guid.NewGuid(), new long[] { 1, 3 }] : [scope, Guid.NewGuid()];
        var query = (IQueryable)typeof(GroupWholeBatchDependencyReader).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(reader, parameters)!;
        var sql = query.ToQueryString();
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Contains("[aioffice].[" + table + "]", sql); Assert.Contains("SELECT TOP(", sql); Assert.Contains("ORDER BY", sql);
        Assert.Matches("DECLARE @\\w+ int = " + maximum + ";", sql);
        foreach (var column in new[] { "TenantId", "CompanyId", "BindingId", method == "CutoffRows" ? "MessageId" : "BatchId" })
            Assert.Contains("[" + column + "] =", sql);
        if (method == "CutoffRows") { Assert.Contains("[CommittedSequence] <=", sql); Assert.Contains("CASE", sql); Assert.DoesNotContain("AfterSequence", sql); }
        if (method == "OriginalClaimRows")
        {
            var epochParameters = System.Text.RegularExpressions.Regex.Match(sql, @"\[Epoch\] IN \((@\w+), (@\w+)\)");
            Assert.True(epochParameters.Success);
            Assert.Contains("DECLARE " + epochParameters.Groups[1].Value + " bigint = CAST(1 AS bigint);", sql);
            Assert.Contains("DECLARE " + epochParameters.Groups[2].Value + " bigint = CAST(3 AS bigint);", sql);
        }
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WholeDependencyReaderRequiresOwnedSqlTransactionBeforeKeysEffectsOrExpiry(bool sql, bool expired)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var counts = await f.CountsAsync(); var reads = f.Keys.Reads;
        if (expired) f.Auth.Clock.Current = handle.Receipt.ExpiresAtUtc;
        using var alternate = sql ? new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1").Options) : null;
        var database = alternate ?? f.Auth.Db;
        var sourceReader = new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var brainReader = new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var reader = new GroupWholeBatchDependencyReader(database, f.Worker, f.Auth.Clock, sourceReader, brainReader);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadLockedAsync(handle));
        Assert.Equal("Whole group batch dependencies are not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(counts, await f.CountsAsync()); Assert.Equal(reads, f.Keys.Reads);
        Assert.Null((await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        Assert.Null(database.Database.CurrentTransaction); Assert.False(database.ChangeTracker.HasChanges());
        if (alternate is not null) Assert.Equal(System.Data.ConnectionState.Closed, alternate.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential")]
    public async Task WholeDependencyReaderCancellationAndForeignWorkerRefuseBeforeDisposedDatabase(string change)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync(); var worker = f.Worker;
        using var canceled = new CancellationTokenSource();
        switch (change)
        {
            case "cancel": canceled.Cancel(); break;
            case "tenant": worker = worker with { TenantId = Guid.NewGuid() }; break;
            case "company": worker = worker with { CompanyId = Guid.NewGuid() }; break;
            case "service": worker = worker with { ServiceId = Guid.NewGuid() }; break;
            case "credential": worker = worker with { CredentialEpoch = worker.CredentialEpoch + 1 }; break;
        }
        f.Auth.Db.Dispose();
        var reader = new GroupWholeBatchDependencyReader(f.Auth.Db, worker, f.Auth.Clock, f.Reader, BrainReader(f));
        if (change == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadLockedAsync(handle, canceled.Token));
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadLockedAsync(handle));
    }

    [Fact]
    public async Task ActualCutoffQueryMatchesShippingReaderWinnerFromPriorAllocation()
    {
        using var f = new Fixture(); var winner = await f.CommitAsync(f.Payload(eventId: "recalled-prior", kind: GroupSourceEventKind.Recall, text: ""));
        await f.AllocateAsync(); await f.CommitAsync(f.Payload(eventId: "new-edit", kind: GroupSourceEventKind.Edit));
        var allocation = await f.AllocateAsync(); var handle = await f.ClaimAllocatedAsync(allocation.BatchId);
        var expected = Assert.Single((await f.Reader.ReadAsync(handle, [winner.MessageId])).Items);
        var reader = new GroupWholeBatchDependencyReader(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var query = (IQueryable<GroupPendingRevisionMetadata>)typeof(GroupWholeBatchDependencyReader)
            .GetMethod("CutoffRows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(reader, [f.Auth.Scope, winner.MessageId, allocation.AllocatedThroughSequence])!;
        var row = Assert.Single(await query.ToArrayAsync());
        Assert.Equal(expected.Revision, row.Revision); Assert.Equal(expected.Kind, row.Kind);
        Assert.Equal(expected.CommittedSequence, row.CommittedSequence); Assert.True(row.CommittedSequence <= allocation.AfterSequence);
    }
}
