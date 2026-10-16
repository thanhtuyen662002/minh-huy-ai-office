using System.Data;
using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static async Task<GroupAutomaticModelContext> FenceInputAsync(Fixture f)
    {
        var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        return GroupAutomaticModelContext.Create(preparation, await BrainReader(f).ReadAsync(claim, [], []), "existing-owned-model", 64000);
    }

    [Fact]
    public async Task ModelDependencyFenceNeverTreatsInMemoryAsQualifiedCurrentSqlAuthority()
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var before = await f.CountsAsync(); var reads = f.Keys.Reads;
        var fence = new GroupAutomaticModelDependencyFence(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fence.RequireCurrentAsync(input));
        Assert.Equal("Group model input dependencies are not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(reads, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential-epoch")]
    public async Task ModelDependencyFenceRejectsReboundWorkerBeforeAnyOtherOperation(string fault)
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var worker = fault switch
        {
            "tenant" => f.Worker with { TenantId = Guid.NewGuid() },
            "company" => f.Worker with { CompanyId = Guid.NewGuid() },
            "service" => f.Worker with { ServiceId = Guid.NewGuid() },
            _ => f.Worker with { CredentialEpoch = 2 }
        };
        var fence = new GroupAutomaticModelDependencyFence(f.Auth.Db, worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fence.RequireCurrentAsync(input));
        Assert.Null(error.InnerException); Assert.Equal(reads, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
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
    public async Task ModelDependencyFenceBadSqlCompositionRefusesBeforeConnectionOrKeyResolution(string fault)
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var connection = new ModelFenceConnectionTrap();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;MultipleActiveResultSets=" + (fault == "mars"))
            .AddInterceptors(connection).Options;
        await using var database = new PlatformDbContext(options);
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
        var fence = new GroupAutomaticModelDependencyFence(database, f.Worker, f.Auth.Clock, sources!, brain!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fence.RequireCurrentAsync(input));
        Assert.Equal("Group model input dependencies are not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, connection.Attempts); Assert.Equal(reads, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Equal(initiallyDirty, database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ModelDependencyFenceCancellationAndNullInputPrecedeAuthorityOrSqlAccess()
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var connection = new ModelFenceConnectionTrap();
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false")
            .AddInterceptors(connection).Options);
        var fence = new GroupAutomaticModelDependencyFence(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fence.RequireCurrentAsync(input, canceled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fence.RequireCurrentAsync(null!));
        Assert.Equal(0, connection.Attempts); Assert.Equal(reads, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.False(database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }

    private sealed class ModelFenceConnectionTrap : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        { Attempts++; throw new InvalidOperationException("Model dependency guard attempted SQL."); }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { Attempts++; throw new InvalidOperationException("Model dependency guard attempted SQL."); }
    }
}
