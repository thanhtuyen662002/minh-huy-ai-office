using System.Data;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("restorer", "guarded")]
    [InlineData("origins", "guarded")]
    [InlineData("restorer", "cancelled")]
    [InlineData("origins", "cancelled")]
    [InlineData("restorer", "non-sql")]
    [InlineData("origins", "non-sql")]
    [InlineData("restorer", "foreign-worker")]
    [InlineData("origins", "foreign-worker")]
    [InlineData("restorer", "dirty")]
    [InlineData("origins", "dirty")]
    [InlineData("origins", "ambient")]
    [InlineData("origins", "source-composition")]
    public async Task OriginalBatchRecoveryUsesOriginalSqlEntryBeforeAnyKeysOrModelDependency(string path, string fault)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var trap = new ModelFenceConnectionTrap(); using var sql = V3StoreDatabase(trap); var database = fault == "non-sql" ? f.Auth.Db : sql;
        var worker = fault == "foreign-worker" ? f.Worker with { CompanyId = Guid.NewGuid() } : f.Worker;
        var sources = new GroupBatchSourceReader(fault == "source-composition" ? f.Auth.Db : database, worker, f.Auth.Clock, f.Keys, new());
        var currentBrain = new GroupBrainCurrentReader(database, worker, f.Auth.Clock, f.Keys, new());
        if (fault == "dirty") database.Add(new GroupModelAttemptStartRecord());
        var reads = f.Keys.Reads; using var caller = new CancellationTokenSource(); if (fault == "cancelled") caller.Cancel();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        Task Action() => path == "restorer"
            ? new GroupOriginalBatchRestorer(database, worker, f.Auth.Clock, sources, currentBrain).ReadAsync(preparation.Context.Handle, caller.Token)
            : new GroupAutomaticInputPlanStore(database, worker, f.Auth.Clock, sources, currentBrain).ReadRestoredOriginsAsync(original, preparation, brain, caller.Token);
        if (fault == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(Action);
        else if (fault == "foreign-worker") await Assert.ThrowsAsync<UnauthorizedAccessException>(Action);
        else await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(fault == "guarded" ? 1 : 0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads);
        if (fault != "non-sql") Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction);
        // Connection traps establish entry ordering only, not SQL recovery,
        // decryption release, a committed result or production model evidence.
    }

    [Fact]
    public async Task OriginalBatchOriginReadCannotSubstituteOneChunkForCompleteOriginalSources()
    {
        using var f = new Fixture(); var (original, chunks) = await OriginalAttemptInputAsync(f);
        Assert.Equal(3, original.Dependencies.Sources.Count); var chunk = chunks[0];
        var trap = new ModelFenceConnectionTrap(); using var db = V3StoreDatabase(trap); var reads = f.Keys.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => V3Store(f, db)
            .ReadRestoredOriginsAsync(original, chunk.Current.Preparation, chunk.Brain));
        Assert.Equal(0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads);
    }

    [Fact]
    public async Task OriginalBatchOriginReadRefusesAnotherBrainHandleBeforeSql()
    {
        using var f = new Fixture(); using var foreign = new Fixture();
        var (preparation, brain, selection) = await IssuedDisclosure(f); var (_, otherBrain, _) = await IssuedDisclosure(foreign);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var trap = new ModelFenceConnectionTrap(); using var db = V3StoreDatabase(trap);
        await Assert.ThrowsAsync<InvalidOperationException>(() => V3Store(f, db).ReadRestoredOriginsAsync(original, preparation, otherBrain));
        Assert.Equal(0, trap.Attempts);
    }

    [Fact]
    public void OriginalBatchOriginQueryTranslatesBoundedExactScopeWithoutPrivateRevisionPayload()
    {
        using var f = new Fixture(); var trap = new ModelFenceConnectionTrap(); using var db = V3StoreDatabase(trap);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }; var sql = V3Store(f, db).RestoredOriginRows(f.Auth.Scope, ids).ToQueryString();
        Assert.Contains("TOP(", sql); Assert.Contains("GroupCustomerRequests", sql);
        foreach (var id in new[] { f.Auth.Scope.TenantId, f.Auth.Scope.CompanyId, f.Auth.Scope.SourceBindingId }.Concat(ids))
            Assert.Contains(id.ToString("D"), sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"WHERE \[[^\]]+\]\.\[TenantId\] = @\w+ AND \[[^\]]+\]\.\[CompanyId\] = @\w+ AND \[[^\]]+\]\.\[BindingId\] = @\w+", sql);
        Assert.Contains("OriginBatchId", sql); Assert.Contains("OriginOperationId", sql); Assert.Contains("OriginCandidateOrdinal", sql);
        Assert.DoesNotContain("ProtectedContent", sql); Assert.DoesNotContain("GroupRequestRevisions", sql); Assert.Equal(0, trap.Attempts);
        // Provider-generated SQL inspected offline; no network/rows/authority.
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zero")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    public void OriginalBatchOriginQueryRefusesInvalidIdentitySetsBeforeTranslation(string fault)
    {
        using var f = new Fixture(); var trap = new ModelFenceConnectionTrap(); using var db = V3StoreDatabase(trap); var id = Guid.NewGuid();
        Guid[] ids = fault switch { "empty" => [], "zero" => [Guid.Empty], "duplicate" => [id, id], _ => Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray() };
        Assert.Throws<InvalidOperationException>(() => V3Store(f, db).RestoredOriginRows(f.Auth.Scope, ids)); Assert.Equal(0, trap.Attempts);
    }
}
