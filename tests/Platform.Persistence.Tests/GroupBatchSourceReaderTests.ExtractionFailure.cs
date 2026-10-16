using System.Data;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task ExtractionFailureProjectionPreservesAllSourcesAndNeverInventsAiInterpretation()
    {
        using var f = new Fixture();
        var model = await f.CommitAsync(f.Payload(messageId: "failed-text", eventId: "failed-text", text: "Tra cứu tồn kho"));
        var media = await f.CommitAsync(f.Payload(messageId: "failed-caption", eventId: "failed-caption", kind: GroupSourceEventKind.Media, text: "Tra cứu nhập xuất"));
        var secret = await f.CommitAsync(f.Payload(messageId: "failed-secret", eventId: "failed-secret", kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"));
        var empty = await f.CommitAsync(f.Payload(messageId: "failed-empty", eventId: "failed-empty", text: ""));
        var recalled = await f.CommitAsync(f.Payload(messageId: "failed-recall", eventId: "failed-recall", kind: GroupSourceEventKind.Recall, text: ""));
        var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim,
            [model.MessageId, media.MessageId, secret.MessageId, empty.MessageId, recalled.MessageId]));
        var input = GroupAutomaticModelContext.Create(preparation, await BrainReader(f).ReadAsync(claim, [], []), "existing-owned-model", 64000);
        var plan = GroupAutomaticNotePlan.CreateExtractionFailure(input);
        Assert.Same(input, plan.ExtractionFailureContext); Assert.Empty(plan.AiNotes);
        var failed = Assert.Single(plan.HostNotes, x => x.Reason == GroupHostAttentionReason.ExtractionFailed);
        Assert.Equal(new HashSet<Guid> { model.MessageId, media.MessageId }, failed.SourceReferences.Select(x => x.MessageId).ToHashSet());
        Assert.All(failed.SourceReferences, x => Assert.Equal(1, x.Revision));
        var receipts = plan.SourceDispositions.ToDictionary(x => x.MessageId);
        Assert.Equal(5, receipts.Count);
        Assert.Equal(GroupWorkSourceOutcome.Attention, receipts[model.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.Attention, receipts[media.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.Quarantined, receipts[secret.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.NoWork, receipts[empty.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.Recalled, receipts[recalled.MessageId].Outcome);
        var effects = GroupNoteEffectPlan.FromAutomatic(plan);
        Assert.Equal(GroupWorkCommitOutcome.Attention, effects.Outcome);
        Assert.All(effects.Notes, x =>
        {
            Assert.Equal(GroupRequestRevisionOrigin.HostAttention, x.Origin);
            Assert.Equal(GroupRequestVerificationLevel.HostObserved, x.VerificationLevel);
            Assert.Equal(GroupRequestEvidenceKind.HostMetadataAttention, x.EvidenceKind);
            Assert.DoesNotContain("PRIVATE_SENTINEL", x.Payload);
            _ = GroupBrainPayloadCodec.DecodeHostAttention(x.Payload);
        });
        // Projection has consumed no attempt and has persisted no effects.
        Assert.Empty(await f.Auth.Db.GroupModelAttemptStarts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
        Assert.Throws<InvalidOperationException>(() => GroupNoteEffectPlan.Entry.FromHost(failed));
    }

    [Fact]
    public async Task ExtractionFailureProjectionPreservesHundredSourcesWithoutTruncatingHostEvidence()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var index = 0; index < 100; index++) ids.Add((await f.CommitAsync(f.Payload(messageId: $"failure-bounded-{index}",
            eventId: $"failure-bounded-{index}", kind: GroupSourceEventKind.Media,
            text: index < 51 ? "Tra cứu tồn kho" : "Password=PRIVATE_SENTINEL_72691"))).MessageId);
        var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids));
        var context = GroupAutomaticModelContext.Create(preparation, await BrainReader(f).ReadAsync(claim, [], []), "existing-owned-model", 64000);
        var plan = GroupAutomaticNotePlan.CreateExtractionFailure(context);
        Assert.Empty(plan.AiNotes); Assert.Equal(100, plan.SourceDispositions.Count);
        Assert.Equal(100, plan.SourceDispositions.Select(x => x.MessageId).Distinct().Count());
        Assert.Equal(51, plan.SourceDispositions.Count(x => x.Outcome == GroupWorkSourceOutcome.Attention));
        Assert.Equal(49, plan.SourceDispositions.Count(x => x.Outcome == GroupWorkSourceOutcome.Quarantined));
        var failures = plan.HostNotes.Where(x => x.Reason == GroupHostAttentionReason.ExtractionFailed).ToArray();
        Assert.Equal(6, failures.Length);
        Assert.Equal(51, failures.SelectMany(x => x.SourceReferences).Count());
        Assert.All(plan.HostNotes, x => Assert.InRange(x.SourceReferences.Count, 1, 10));
        Assert.InRange(plan.NoteCount, 1, GroupAutomaticNotePlan.MaximumNotes);
        Assert.Equal(200, GroupNoteEffectPlan.FromAutomatic(plan).Notes.Sum(x => x.Evidence.Count));
        Assert.Throws<NotSupportedException>(() => ((IList<GroupAutomaticSourceReceipt>)plan.SourceDispositions).Clear());
        Assert.Empty(await f.Auth.Db.GroupWorkRawDispositions.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task ExtractionFailureWriterRejectsInMemoryWithoutTreatingProjectionAsSqlExhaustion()
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var store = new GroupNoteCommitStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f), f.Keys, new());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitExtractionFailureAsync(input, Guid.NewGuid()));
        Assert.Equal("Group note commit is unavailable.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(reads, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupModelAttemptStarts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential-epoch")]
    public async Task ExtractionFailureWriterRejectsReboundWorkerBeforeAnySqlOrKeyAwait(string fault)
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var worker = fault switch
        {
            "tenant" => f.Worker with { TenantId = Guid.NewGuid() },
            "company" => f.Worker with { CompanyId = Guid.NewGuid() },
            "service" => f.Worker with { ServiceId = Guid.NewGuid() },
            _ => f.Worker with { CredentialEpoch = 2 }
        };
        var trap = new ModelFenceConnectionTrap();
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false")
            .AddInterceptors(trap).Options);
        var store = new GroupNoteCommitStore(database, worker, f.Auth.Clock,
            new(database, worker, f.Auth.Clock, f.Keys, new()), new(database, worker, f.Auth.Clock, f.Keys, new()), f.Keys, new());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CommitExtractionFailureAsync(input, Guid.NewGuid()));
        Assert.Equal(0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.False(database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
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
    public async Task ExtractionFailureWriterRefusesBadCompositionBeforeSqlOrKeyAwait(string fault)
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var trap = new ModelFenceConnectionTrap();
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;MultipleActiveResultSets=" + (fault == "mars"))
            .AddInterceptors(trap).Options);
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
        var wasDirty = database.ChangeTracker.HasChanges();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled) : null;
        var store = new GroupNoteCommitStore(database, f.Worker, f.Auth.Clock, sources!, brain!, f.Keys, new());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitExtractionFailureAsync(input, Guid.NewGuid()));
        Assert.Equal("Group note commit is unavailable.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Equal(wasDirty, database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ExtractionFailureWriterRejectsCancellationNullAndEmptyOperationBeforeSql()
    {
        using var f = new Fixture(); var input = await FenceInputAsync(f); var reads = f.Keys.Reads;
        var trap = new ModelFenceConnectionTrap();
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false")
            .AddInterceptors(trap).Options);
        var store = new GroupNoteCommitStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()), f.Keys, new());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitExtractionFailureAsync(input, Guid.NewGuid(), canceled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CommitExtractionFailureAsync(null!, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitExtractionFailureAsync(input, Guid.Empty));
        Assert.Equal(0, trap.Attempts); Assert.Equal(reads, f.Keys.Reads);
        Assert.False(database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData("MultipleActiveResultSets=NOT_A_BOOLEAN")]
    [InlineData("Connect Timeout=NOT_A_NUMBER")]
    [InlineData("Connect Timeout=2147483648")]
    [InlineData("UnexpectedClosedOption=PRIVATE_TEST_SENTINEL")]
    public async Task ExtractionFailureWriterNormalizesMalformedConnectionBeforeSqlOrKeyAwait(string invalid)
    {
        using var f = new Fixture();
        var input = await FenceInputAsync(f);
        var reads = f.Keys.Reads;
        var trap = new ModelFenceConnectionTrap();
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;" + invalid)
            .AddInterceptors(trap).Options);
        var store = new GroupNoteCommitStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()),
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), f.Keys, new());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitExtractionFailureAsync(input, Guid.NewGuid()));
        Assert.Equal("Group note commit is unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, trap.Attempts);
        Assert.Equal(reads, f.Keys.Reads);
        Assert.False(database.ChangeTracker.HasChanges());
        Assert.Null(database.Database.CurrentTransaction);
    }
}
