using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task InputRecoveryReappliesPersistedUnsortedOrderToFreshReaderContent()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var ids = await PlanGlossariesAsync(f, claim, source);
        var original = await BrainReader(f).ReadAsync(claim, [], ids);
        var context = GroupAutomaticModelContext.Create(preparation, original, "model", 64000);
        var stored = GroupAutomaticInputPlan.Read(GroupAutomaticInputPlan.Create(preparation, original, PlanProfile, "model", 64000, []).Encode());
        var fresh = await BrainReader(f).ReadAsync(claim, [], ids.Reverse().ToArray());
        Assert.NotEqual(original.Items.Select(x => x.RecordId), fresh.Items.Select(x => x.RecordId));
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var restored = Assert.Single(stored.RestoreChunks(preparation, fresh, PlanProfile, []));
        Assert.Equal(ids, restored.Brain.Items.Select(x => x.RecordId));
        Assert.All(restored.Brain.Items, x => Assert.Same(fresh.Items.Single(y => y.RecordId == x.RecordId), x));
        Assert.Same(claim, restored.Brain.Handle);
        Assert.Equal(context.CreateGatewayRequest(Guid.NewGuid()).Input, restored.Current.ModelContext!.CreateGatewayRequest(Guid.NewGuid()).Input);
        Assert.Equal(stored.Chunks[0].OperationId, restored.Original.OperationId);
        Assert.Equal(stored.Chunks[0].InputSha256, GroupModelAttemptBudget.InputIdentity(restored.Current.ModelContext));
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ids.Reverse(), fresh.Items.Select(x => x.RecordId));
        Assert.DoesNotContain("Published", restored.ToString());
    }

    [Fact]
    public async Task InputRecoveryRetainsWholeInterleavedOrderWithFreshGroupedPublicReader()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var seed = await ModelBrainAsync(f, claim, source, preparation);
        var batch = Guid.NewGuid(); var operation = Guid.NewGuid();
        var id = new Guid(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-note-id-v1/{f.Auth.Scope.TenantId:D}/{f.Auth.Scope.CompanyId:D}/{f.Auth.Scope.SourceBindingId:D}/{batch:D}/{operation:D}/request/1"))).AsSpan(0, 16));
        var request = new GroupCustomerRequestRecord
        {
            TenantId = f.Auth.Scope.TenantId,
            CompanyId = f.Auth.Scope.CompanyId,
            BindingId = f.Auth.Scope.SourceBindingId,
            Id = id,
            OriginBatchId = batch,
            OriginOperationId = operation,
            OriginCandidateOrdinal = 1,
            RequestCode = "REQ-" + id.ToString("N").ToUpperInvariant(),
            Kind = GroupNoteKind.Incident,
            SourceVersion = claim.Authority.Source.Version,
            DeletionGeneration = claim.Authority.Source.DeletionGeneration,
            CreatedAtUtc = f.Auth.Clock.Current,
            UpdatedAtUtc = f.Auth.Clock.Current
        };
        var revision = new GroupRequestRevisionRecord
        {
            TenantId = request.TenantId,
            CompanyId = request.CompanyId,
            BindingId = request.BindingId,
            RequestId = id,
            Revision = 1,
            Origin = GroupRequestRevisionOrigin.AiExtracted,
            VerificationLevel = GroupRequestVerificationLevel.SourceBackedAiInterpretation,
            AuthorServiceId = f.Worker.ServiceId,
            SourceBatchId = batch,
            ClaimEpoch = claim.Receipt.Epoch,
            SourceVersion = request.SourceVersion,
            DeletionGeneration = request.DeletionGeneration,
            ContentKeyId = "owned",
            CreatedAtUtc = request.CreatedAtUtc,
            ProtectedContent = new GroupBrainContentProtector().Protect(new(f.Auth.Scope, GroupBrainContentKind.RequestRevision,
                id, 1, request.SourceVersion, request.DeletionGeneration), seed.Items.Single(x => x.Kind == GroupBrainContentKind.RequestRevision).Content,
                Enumerable.Repeat((byte)0x32, 32).ToArray(), "owned")
        };
        revision.EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(revision.ProtectedContent));
        f.Auth.Db.AddRange(request, revision, new GroupRequestEvidenceRecord
        {
            TenantId = request.TenantId,
            CompanyId = request.CompanyId,
            BindingId = request.BindingId,
            RequestId = id,
            RequestRevision = 1,
            Ordinal = 1,
            MessageId = source.MessageId,
            MessageRevision = source.Revision,
            Kind = GroupRequestEvidenceKind.LiteralSourceQuote
        });
        await f.Auth.Db.SaveChangesAsync();
        var glossary = seed.Items.Single(x => x.Kind == GroupBrainContentKind.GlossaryRevision).RecordId;
        var grouped = await BrainReader(f).ReadAsync(claim, [id], [glossary]);
        var original = new GroupBrainPrivateContext(claim, grouped.Snapshots.Reverse().ToArray(), grouped.Items.Reverse().ToArray());
        GroupBrainRequestOriginMetadata[] origins = [new(f.Auth.Scope, id, batch, operation, 1, request.CreatedAtUtc)];
        var plan = GroupAutomaticInputPlan.Read(GroupAutomaticInputPlan.Create(preparation, original, PlanProfile, "model", 64000, origins).Encode());
        var fresh = await BrainReader(f).ReadAsync(claim, [id], [glossary]);
        Assert.Equal(GroupBrainContentKind.RequestRevision, fresh.Items[0].Kind);
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var restored = Assert.Single(plan.RestoreChunks(preparation, fresh, PlanProfile, origins));
        Assert.Equal(new[] { GroupBrainContentKind.GlossaryRevision, GroupBrainContentKind.RequestRevision }, restored.Brain.Items.Select(x => x.Kind));
        Assert.Equal(GroupAutomaticModelContext.Create(preparation, original, "model", 64000).CreateGatewayRequest(Guid.NewGuid()).Input,
            restored.Current.ModelContext!.CreateGatewayRequest(Guid.NewGuid()).Input);
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        // Real InMemory reader/decrypt/metadata checks; the seeded earlier
        // request is not a native SQL plan/restart authenticity proof.
    }

    [Fact]
    public async Task InputRecoveryRetainsExactPartitionOperationIdentityAndReadonlyResults()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await f.CommitAsync(f.Payload($"recover-{i}", $"recover-event-{i}", "Nhập xuất 😀 " + new string('x', 3000)))).MessageId);
        var claim = await f.ClaimAsync(); var original = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var singleton = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [ids[0]]));
        var budget = GroupAutomaticModelContext.Create(singleton, brain, "model", 64000).Manifest.UsedTokens;
        var plan = GroupAutomaticInputPlan.Read(GroupAutomaticInputPlan.Create(original, brain, PlanProfile, "model", budget, []).Encode());
        var fresh = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids.AsEnumerable().Reverse().ToArray()));
        var restored = plan.RestoreChunks(fresh, brain, PlanProfile, []);
        Assert.Equal(3, restored.Count);
        Assert.Equal(plan.Chunks.Select(x => x.OperationId), restored.Select(x => x.Original.OperationId));
        Assert.Equal(ids, restored.SelectMany(x => x.Current.Preparation.Context.Items).Select(x => x.MessageId));
        Assert.All(restored, x => Assert.Equal(x.Original.InputSha256, GroupModelAttemptBudget.InputIdentity(x.Current.ModelContext!)));
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<GroupAutomaticInputPlan.RestoredChunk>>(restored).Clear());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputRecoveryRefusesChangedPrivateInputDespiteIdenticalDependencyMetadata(bool history)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var raw = await f.Reader.ReadAsync(claim, [source.MessageId]); var preparation = GroupBatchSourcePreparation.Create(raw);
        var ids = await PlanGlossariesAsync(f, claim, source); var brain = await BrainReader(f).ReadAsync(claim, [], ids);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []);
        if (history)
            brain = new(claim, brain.Snapshots.ToArray(), brain.Snapshots.Select(x => new GroupBrainPrivateRevision(x,
                GroupBrainPayloadCodec.EncodeGlossary("changed", "changed private input"))).ToArray());
        else preparation = GroupBatchSourcePreparation.Create(new(claim, raw.AllocatedThroughSequence, raw.Coverage,
            raw.Snapshots.ToArray(), [new(raw.Snapshots[0], "changed private source")]));
        var error = Assert.Throws<InvalidOperationException>(() => plan.RestoreChunks(preparation, brain, PlanProfile, []));
        Assert.Equal("Group original input plan is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputRecoveryRefusesMissingOrAdditionalHistoryInsteadOfReplacingSelection(bool additional)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var ids = await PlanGlossariesAsync(f, claim, source); var original = await BrainReader(f).ReadAsync(claim, [], ids);
        var plan = GroupAutomaticInputPlan.Create(preparation, original, PlanProfile, "model", 64000, []);
        var selected = additional ? ids.Concat(await PlanGlossariesAsync(f, claim, source)).ToArray() : ids.Take(1).ToArray();
        var changed = await BrainReader(f).ReadAsync(claim, [], selected);
        Assert.Throws<InvalidOperationException>(() => plan.RestoreChunks(preparation, changed, PlanProfile, []));
    }

    [Fact]
    public async Task InputRecoveryHostOnlyReturnsNoModelAndRequiresSameProfileOriginsAndHandle()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: "password=RECOVERY_PRIVATE_SECRET_3927"));
        var claim = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, null, 0, []);
        var result = Assert.Single(plan.RestoreChunks(preparation, brain, PlanProfile, []));
        Assert.False(result.Original.RequiresModel); Assert.Null(result.Current.ModelContext);
        Assert.Throws<InvalidOperationException>(() => plan.RestoreChunks(preparation, brain, new string('B', 64), []));
        Assert.Throws<InvalidOperationException>(() => plan.RestoreChunks(preparation, brain, PlanProfile,
            [new(f.Auth.Scope, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, f.Auth.Clock.Current)]));
        Assert.Throws<InvalidOperationException>(() => plan.RestoreChunks(preparation,
            new GroupBrainPrivateContext(new(claim.Receipt, claim.Authority), [], []), PlanProfile, []));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => plan.RestoreChunks(preparation, brain, PlanProfile, [], canceled.Token));
        Assert.DoesNotContain("RECOVERY_PRIVATE", result.ToString());
    }
}
