using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalWithoutModelNullGateIssuesOnlyHostCarrierAndNeverQualifiesModel(bool modelText)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f,
            modelText ? "Tra cứu tồn kho" : "password=OWNED_WITHOUT_MODEL");
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, modelText ? "model" : null, modelText ? 64000 : 0, []);
        var issuer = new GroupOriginalAdmissionPreparer(null, f.Auth.Clock); var reads = f.Keys.Reads; var counts = await f.CountsAsync();
        if (modelText) await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.PrepareAsync(plan, preparation, brain, []));
        else
        {
            var carrier = await issuer.PrepareAsync(plan, preparation, brain, []);
            issuer.RequireIssued(carrier, preparation, brain); carrier.Sidecar.RequirePlan(plan);
            Assert.All(plan.Chunks, x => Assert.False(x.RequiresModel));
            Assert.All(carrier.Sidecar.Entries, x => Assert.Null(x.Snapshot));
            Assert.Throws<InvalidOperationException>(() => new GroupOriginalAdmissionPreparer(null, f.Auth.Clock).RequireIssued(carrier, preparation, brain));
        }
        Assert.Equal(reads, f.Keys.Reads); Assert.Equal(counts, await f.CountsAsync());
        // Structural preparation only; no stored original/SQL/model authority.
    }

    [Fact]
    public async Task OriginalWithoutModelDecodedHostPlanCannotBecomeFirstInsertIssuance()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, "password=OWNED_WITHOUT_MODEL");
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, null, 0, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupOriginalAdmissionPreparer(null, f.Auth.Clock)
            .PrepareAsync(GroupAutomaticInputPlan.Read(plan.Encode()), preparation, brain, []));
    }

    [Fact]
    public async Task OriginalWithoutModelSelectionOmitsUnneededExistingHistoryAndDoesNotResolvePrivateKeys()
    {
        using var f = new Fixture(); var old = await f.CommitAsync(f.Payload("old", "old-event", "Tra cứu tồn kho")); var previous = await f.ClaimAsync();
        var oldPrep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(previous, [old.MessageId]));
        _ = await ModelBrainAsync(f, previous, old, oldPrep);
        var host = await f.CommitAsync(f.Payload("host", "host-event", "password=OWNED_WITHOUT_MODEL")); var handle = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [host.MessageId]));
        var legacy = await HistorySelector(f).SelectAsync(preparation); Assert.NotEmpty(legacy.RequestIds); Assert.NotEmpty(legacy.GlossaryIds);
        var reads = f.Keys.Reads; var selection = await HistorySelector(f).SelectWithoutModelAsync(preparation);
        Assert.Empty(selection.RequestIds); Assert.Empty(selection.GlossaryIds); Assert.False(selection.HasAdditionalHistory);
        var brain = await BrainReader(f).ReadAsync(handle, [], []); Assert.Empty(brain.Items); Assert.Equal(reads, f.Keys.Reads);
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, null, 0, []);
        var carrier = await new GroupOriginalAdmissionPreparer(null, f.Auth.Clock).PrepareAsync(plan, preparation, brain, []);
        Assert.Empty(plan.HistoryOrder); Assert.All(carrier.Sidecar.Entries, x => Assert.Null(x.Snapshot));
    }

    [Fact]
    public async Task OriginalWithoutModelSelectionRefusesTextCandidatesBeforeSqlOrKeys()
    {
        using var f = new Fixture(); var (preparation, _, _) = await IssuedDisclosure(f); var reads = f.Keys.Reads;
        var trap = new ModelFenceConnectionTrap(); using var db = V3StoreDatabase(trap);
        var sources = new GroupBatchSourceReader(db, f.Worker, f.Auth.Clock, f.Keys, new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupAutomaticHistorySelector(db, f.Worker, f.Auth.Clock, sources)
            .SelectWithoutModelAsync(preparation));
        Assert.Equal(0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads);
    }

    [Fact]
    public async Task OriginalWithoutModelSelectionStillRequiresCurrentExtractAndCannotIssueAfterRevocation()
    {
        using var f = new Fixture(); var (preparation, _, _) = await IssuedDisclosure(f, "password=OWNED_WITHOUT_MODEL");
        f.Auth.Service.IsEnabled = false; await f.Auth.Db.SaveChangesAsync(); var reads = f.Keys.Reads;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => HistorySelector(f).SelectWithoutModelAsync(preparation));
        Assert.Equal(reads, f.Keys.Reads);
    }

    [Fact]
    public async Task OriginalWithoutModelSelectionCancellationAndNullDoNotCreateIssuance()
    {
        using var f = new Fixture(); var (preparation, _, _) = await IssuedDisclosure(f, "password=OWNED_WITHOUT_MODEL");
        using var caller = new CancellationTokenSource(); caller.Cancel(); var reads = f.Keys.Reads;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HistorySelector(f).SelectWithoutModelAsync(preparation, caller.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => HistorySelector(f).SelectWithoutModelAsync(null!));
        Assert.Equal(reads, f.Keys.Reads);
    }

    [Theory]
    [InlineData("guarded")]
    [InlineData("non-sql")]
    [InlineData("cancelled")]
    [InlineData("dirty")]
    [InlineData("foreign-worker")]
    [InlineData("source-composition")]
    [InlineData("ambient")]
    [InlineData("mars")]
    public async Task OriginalWithoutModelCreatorAuthenticatesExistingOriginalBeforePrivateInputOrFirstInsert(string fault)
    {
        using var f = new Fixture(); var (preparation, _, _) = await IssuedDisclosure(f, "password=OWNED_WITHOUT_MODEL");
        var trap = new ModelFenceConnectionTrap();
        using var sql = fault == "mars" ? new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
            "Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;MultipleActiveResultSets=true").AddInterceptors(trap).Options) : V3StoreDatabase(trap);
        var database = fault == "non-sql" ? f.Auth.Db : sql; var worker = fault == "foreign-worker" ? f.Worker with { ServiceId = Guid.NewGuid() } : f.Worker;
        var sources = new GroupBatchSourceReader(fault == "source-composition" ? f.Auth.Db : database, worker, f.Auth.Clock, f.Keys, new());
        var brain = new GroupBrainCurrentReader(database, worker, f.Auth.Clock, f.Keys, new());
        if (fault == "dirty") database.Add(new GroupModelAttemptStartRecord());
        using var caller = new CancellationTokenSource(); if (fault == "cancelled") caller.Cancel();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        var reads = f.Keys.Reads;
        Task Action() => new GroupOriginalWithoutModelCreator(database, worker, f.Auth.Clock, sources, brain).TryCreateAsync(preparation.Context.Handle, caller.Token);
        if (fault == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(Action);
        else if (fault == "foreign-worker") await Assert.ThrowsAsync<UnauthorizedAccessException>(Action);
        else await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(fault == "guarded" ? 1 : 0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads);
    }
}
