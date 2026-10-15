using System.Transactions;
using Microsoft.EntityFrameworkCore;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("guarded")]
    [InlineData("non-sql")]
    [InlineData("cancelled")]
    [InlineData("dirty")]
    [InlineData("foreign-worker")]
    [InlineData("source-composition")]
    [InlineData("ambient")]
    [InlineData("mars")]
    public async Task ModelOriginalCreatorAuthenticatesExistingSqlOriginalBeforeInputKeysOrGatewayFactory(string fault)
    {
        using var f = new Fixture(); var (prep, _, _) = await IssuedDisclosure(f); var factoryCalls = 0;
        var trap = new ModelFenceConnectionTrap();
        using var sql = fault == "mars" ? new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
            "Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;MultipleActiveResultSets=true").AddInterceptors(trap).Options) : V3StoreDatabase(trap);
        var db = fault == "non-sql" ? f.Auth.Db : sql; var worker = fault == "foreign-worker" ? f.Worker with { ServiceId = Guid.NewGuid() } : f.Worker;
        var sources = new GroupBatchSourceReader(fault == "source-composition" ? f.Auth.Db : db, worker, f.Auth.Clock, f.Keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, f.Auth.Clock, f.Keys, new());
        if (fault == "dirty") db.Add(new GroupModelAttemptStartRecord());
        using var caller = new CancellationTokenSource(); if (fault == "cancelled") caller.Cancel();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        var reads = f.Keys.Reads;
        IAiStructuredRequestAdmission Factory() { factoryCalls++; throw new InvalidOperationException("Factory must remain cold."); }
        // Null input cannot bypass the existing-original authorization order.
        Task Action() => new GroupOriginalModelCreator(db, worker, f.Auth.Clock, sources, brain, Factory)
            .TryCreateAsync(prep.Context.Handle, null!, caller.Token);
        if (fault == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(Action);
        else if (fault == "foreign-worker") await Assert.ThrowsAsync<UnauthorizedAccessException>(Action);
        else await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(fault == "guarded" ? 1 : 0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads); Assert.Equal(0, factoryCalls);
        // SQL entry traps only; no positive first insert/recovery/model claim.
    }

    [Theory]
    [InlineData("guarded")]
    [InlineData("non-sql")]
    [InlineData("cancelled")]
    [InlineData("dirty")]
    [InlineData("foreign-worker")]
    [InlineData("source-composition")]
    [InlineData("ambient")]
    [InlineData("unissued")]
    [InlineData("foreign-brain")]
    public async Task SelectionOriginsRequiresActualIssuanceAndCurrentSqlBeforeReleasingMetadata(string fault)
    {
        using var f = new Fixture(); using var foreign = new Fixture(); var (prep, brain, selection) = await IssuedDisclosure(f);
        if (fault == "unissued") selection = new(prep.Scope, selection.RequestIds.ToArray(), selection.GlossaryIds.ToArray(), selection.HasAdditionalHistory);
        if (fault == "foreign-brain") { var (_, other, _) = await IssuedDisclosure(foreign); brain = other; }
        var trap = new ModelFenceConnectionTrap(); using var sql = V3StoreDatabase(trap); var db = fault == "non-sql" ? f.Auth.Db : sql;
        var worker = fault == "foreign-worker" ? f.Worker with { CompanyId = Guid.NewGuid() } : f.Worker;
        var sources = new GroupBatchSourceReader(fault == "source-composition" ? f.Auth.Db : db, worker, f.Auth.Clock, f.Keys, new());
        var currentBrain = new GroupBrainCurrentReader(db, worker, f.Auth.Clock, f.Keys, new());
        if (fault == "dirty") db.Add(new GroupModelAttemptStartRecord());
        using var caller = new CancellationTokenSource(); if (fault == "cancelled") caller.Cancel();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        var reads = f.Keys.Reads;
        Task Action() => new GroupAutomaticInputPlanStore(db, worker, f.Auth.Clock, sources, currentBrain)
            .ReadSelectionOriginsAsync(prep, brain, selection, caller.Token);
        if (fault == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(Action);
        else if (fault == "foreign-worker") await Assert.ThrowsAsync<UnauthorizedAccessException>(Action);
        else await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(fault == "guarded" ? 1 : 0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads);
    }
}
