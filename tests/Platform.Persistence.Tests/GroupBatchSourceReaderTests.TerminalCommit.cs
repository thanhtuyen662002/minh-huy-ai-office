using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("non-sql")]
    [InlineData("operation")]
    [InlineData("dirty")]
    [InlineData("ambient")]
    [InlineData("mars")]
    [InlineData("missing-sources")]
    [InlineData("missing-brain")]
    [InlineData("foreign-sources")]
    [InlineData("foreign-brain")]
    [InlineData("clock")]
    [InlineData("worker")]
    [InlineData("cancelled")]
    public async Task TerminalStoreRejectsUnsafeEntryBeforeOpeningSqlOrStagingAnyEffect(string fault)
    {
        using var f = new Fixture(); await f.CommitAsync(f.Payload());
        var allocation = await f.AllocateAsync(); var handle = await f.ClaimAllocatedAsync(allocation.BatchId);
        using var sql = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
            "Server=127.0.0.1,1;Database=never_terminal;Integrated Security=true;TrustServerCertificate=true;MultipleActiveResultSets="
            + (fault == "mars" ? "true" : "false")).Options);
        var database = fault == "non-sql" ? f.Auth.Db : sql;
        var worker = fault == "worker" ? f.Worker with { CredentialEpoch = 2 } : f.Worker;
        var clock = fault == "clock" ? TimeProvider.System : (TimeProvider)f.Auth.Clock;
        var source = fault == "missing-sources" ? null! : new GroupBatchSourceReader(
            fault == "foreign-sources" ? f.Auth.Db : database, worker, f.Auth.Clock, f.Keys, new());
        var brain = fault == "missing-brain" ? null! : new GroupBrainCurrentReader(
            fault == "foreign-brain" ? f.Auth.Db : database, worker, f.Auth.Clock, f.Keys, new());
        if (fault == "dirty") database.Add(new GroupBatchTerminalReceiptRecord());
        var initialEntries = database.ChangeTracker.Entries().Count();
        using var ambient = fault == "ambient" ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        using var cancellation = new CancellationTokenSource(); if (fault == "cancelled") cancellation.Cancel();
        var store = new GroupBatchTerminalStore(database, worker, clock, source, brain);
        if (fault == "cancelled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitAsync(handle, Guid.NewGuid(), cancellation.Token));
        else if (fault == "worker")
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CommitAsync(handle, Guid.NewGuid(), cancellation.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(handle,
            fault == "operation" ? Guid.Empty : Guid.NewGuid(), cancellation.Token));
        Assert.Equal(initialEntries, database.ChangeTracker.Entries().Count()); Assert.Null(database.Database.CurrentTransaction);
        Assert.Equal(System.Data.ConnectionState.Closed, sql.Database.GetDbConnection().State);
    }
}
