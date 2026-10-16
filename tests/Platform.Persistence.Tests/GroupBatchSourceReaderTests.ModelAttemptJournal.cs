using System.Data;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    // This test-only permit is used solely to exercise entry refusals. It is
    // never returned by a SQL operation and never authorizes a provider call.
    private static GroupModelAttemptPermit JournalGuardPermit(GroupAutomaticModelContext context, DateTimeOffset now)
    {
        var receipt = context.Preparation.Context.Handle.Receipt;
        return new(context, new(context.Scope, context.BatchId, GroupModelAttemptBudget.InputIdentity(context),
            1, Guid.NewGuid(), null, receipt.OperationId, receipt.Epoch, now, now.AddSeconds(1)));
    }

    [Fact]
    public async Task ModelAttemptJournalDoesNotTreatInMemoryAsDurableSqlHistory()
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var before = await f.CountsAsync(); var reads = f.Keys.Reads;
        var store = new GroupModelAttemptJournalStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var permit = JournalGuardPermit(input, f.Auth.Clock.GetUtcNow());
        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryStartAsync(input, Guid.NewGuid()));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordFailureAsync(input, permit, GroupModelAttemptFailure.InvalidOutput));
        Assert.Equal("Group model attempt journal is not available.", start.Message); Assert.Null(start.InnerException);
        Assert.Equal(start.Message, failure.Message); Assert.Null(failure.InnerException);
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(reads, f.Keys.Reads);
        Assert.Empty(await f.Auth.Db.GroupModelAttemptStarts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupModelAttemptFailures.AsNoTracking().ToArrayAsync());
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential-epoch")]
    public async Task ModelAttemptJournalReboundWorkerCannotStartOrReportProviderFailure(string fault)
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var worker = fault switch
        {
            "tenant" => f.Worker with { TenantId = Guid.NewGuid() },
            "company" => f.Worker with { CompanyId = Guid.NewGuid() },
            "service" => f.Worker with { ServiceId = Guid.NewGuid() },
            _ => f.Worker with { CredentialEpoch = 2 }
        };
        var store = new GroupModelAttemptJournalStore(f.Auth.Db, worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var permit = JournalGuardPermit(input, f.Auth.Clock.GetUtcNow());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.TryStartAsync(input, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.RecordFailureAsync(input, permit, GroupModelAttemptFailure.InvalidOutput));
        Assert.Equal(reads, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("mars")]
    [InlineData("source-null")]
    [InlineData("brain-null")]
    [InlineData("source-database")]
    [InlineData("brain-database")]
    [InlineData("source-clock")]
    [InlineData("brain-clock")]
    [InlineData("dirty")]
    [InlineData("ambient")]
    public async Task ModelAttemptJournalBadCompositionRefusesBeforeAnySqlOrKeyResolution(string fault)
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var connection = new ModelFenceConnectionTrap();
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;MultipleActiveResultSets=" + (fault == "mars"))
            .AddInterceptors(connection).Options);
        GroupBatchSourceReader? sources = new(database, f.Worker, f.Auth.Clock, f.Keys, new());
        GroupBrainCurrentReader? brain = new(database, f.Worker, f.Auth.Clock, f.Keys, new());
        switch (fault)
        {
            case "source-null": sources = null; break;
            case "brain-null": brain = null; break;
            case "source-database": sources = f.Reader; break;
            case "brain-database": brain = BrainReader(f); break;
            case "source-clock": sources = new(database, f.Worker, TimeProvider.System, f.Keys, new()); break;
            case "brain-clock": brain = new(database, f.Worker, TimeProvider.System, f.Keys, new()); break;
            case "dirty":
                database.GroupServiceGrants.Add(new GroupServiceGrantRecord
                {
                    TenantId = f.Worker.TenantId,
                    CompanyId = f.Worker.CompanyId,
                    ServiceId = f.Worker.ServiceId,
                    BindingId = input.Scope.SourceBindingId,
                    Capability = GroupServiceCapability.Extract,
                    IsEnabled = true
                }); break;
        }
        var initiallyDirty = database.ChangeTracker.HasChanges();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled) : null;
        var store = new GroupModelAttemptJournalStore(database, f.Worker, f.Auth.Clock, sources!, brain!);
        var permit = JournalGuardPermit(input, f.Auth.Clock.GetUtcNow());
        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryStartAsync(input, Guid.NewGuid()));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordFailureAsync(input, permit, GroupModelAttemptFailure.TransientProvider));
        Assert.Equal("Group model attempt journal is not available.", start.Message); Assert.Null(start.InnerException);
        Assert.Equal(start.Message, failure.Message); Assert.Null(failure.InnerException);
        Assert.Equal(0, connection.Attempts); Assert.Equal(reads, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Equal(initiallyDirty, database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ModelAttemptJournalCancellationNullUnknownFailureAndNonceRejectBeforeSql()
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var connection = new ModelFenceConnectionTrap();
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false")
            .AddInterceptors(connection).Options);
        var store = new GroupModelAttemptJournalStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()));
        var permit = JournalGuardPermit(input, f.Auth.Clock.GetUtcNow());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryStartAsync(input, Guid.NewGuid(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RecordFailureAsync(input, permit, GroupModelAttemptFailure.InvalidOutput, canceled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.TryStartAsync(null!, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.RecordFailureAsync(input, null!, GroupModelAttemptFailure.InvalidOutput));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryStartAsync(input, Guid.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordFailureAsync(input, permit, (GroupModelAttemptFailure)99));
        Assert.Equal(0, connection.Attempts); Assert.Equal(reads, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.False(database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }
}
