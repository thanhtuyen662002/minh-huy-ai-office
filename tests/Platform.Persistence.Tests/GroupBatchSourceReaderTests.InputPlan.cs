using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private const string PlanProfile = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task InputPlanRetainsActualUnsortedPublishedGlossaryOrderForExactRecovery()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var selected = await PlanGlossariesAsync(f, claim, source);
        var original = await BrainReader(f).ReadAsync(claim, [], selected);
        Assert.Equal(selected, original.Items.Select(x => x.RecordId));
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var plan = GroupAutomaticInputPlan.Create(preparation, original, PlanProfile, "model", 64000, []);
        var wire = plan.Encode(); var stored = GroupAutomaticInputPlan.Read(wire);
        Assert.Equal(selected.OrderBy(x => x), stored.Dependencies.Dependencies.Select(x => x.RecordId));
        Assert.Equal(selected, stored.HistoryOrder.Select(x => x.RecordId));
        Assert.All(stored.HistoryOrder, x => Assert.Equal(GroupBrainContentKind.GlossaryRevision, x.Kind));
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<GroupAutomaticInputPlan.HistoryMember>>(stored.HistoryOrder).Clear());
        wire[0] ^= 1; Assert.Equal(plan.Encode(), stored.Encode());
        var recovered = await BrainReader(f).ReadAsync(claim, [], stored.HistoryOrder.Select(x => x.RecordId).ToArray());
        stored.RequireRestored(preparation, recovered, PlanProfile, []);
        var originalInput = GroupAutomaticModelContext.Create(preparation, original, "model", 64000);
        var recoveredInput = GroupAutomaticModelContext.Create(preparation, recovered, "model", 64000);
        Assert.Equal(originalInput.CreateGatewayRequest(Guid.NewGuid()).Input, recoveredInput.CreateGatewayRequest(Guid.NewGuid()).Input);
        Assert.Equal(plan.Sha256, stored.Sha256);
        Assert.Equal(plan.Chunks[0].InputSha256, GroupModelAttemptBudget.InputIdentity(recoveredInput));
    }

    [Fact]
    public async Task InputPlanRefusesReorderedHistoryAndBindsOrderIntoOriginalOperationIdentity()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var selected = await PlanGlossariesAsync(f, claim, source);
        var original = await BrainReader(f).ReadAsync(claim, [], selected);
        var reordered = await BrainReader(f).ReadAsync(claim, [], selected.Reverse().ToArray());
        var plan = GroupAutomaticInputPlan.Create(preparation, original, PlanProfile, "model", 64000, []);
        var other = GroupAutomaticInputPlan.Create(preparation, reordered, PlanProfile, "model", 64000, []);
        Assert.Equal(plan.Dependencies.Dependencies, other.Dependencies.Dependencies);
        Assert.NotEqual(plan.Chunks[0].InputSha256, other.Chunks[0].InputSha256);
        Assert.NotEqual(plan.Chunks[0].OperationId, other.Chunks[0].OperationId);
        var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Read(plan.Encode()).RequireRestored(preparation, reordered, PlanProfile, []));
        Assert.Equal("Group original input plan is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task InputPlanV2RefusesUnshippedV1WireFormat()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var wire = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []).Encode();
        Assert.Equal("AIOGPLN2", Encoding.ASCII.GetString(wire, 0, 8));
        wire[7] = (byte)'1';
        var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Read(wire));
        Assert.Equal("Group original input plan is not available.", error.Message); Assert.Null(error.InnerException);
    }

    private static async Task<Guid[]> PlanGlossariesAsync(Fixture f, GroupBatchClaimHandle claim, GroupIngressCommittedReceipt source)
    {
        var first = await SeedBrainAsync(f, claim, source); var second = await SeedBrainAsync(f, claim, source);
        foreach (var (seed, term) in new[] { (first, "alpha"), (second, "beta") })
        {
            seed.GlossaryRevision.ProtectedContent = new GroupBrainContentProtector().Protect(new(f.Auth.Scope,
                GroupBrainContentKind.GlossaryRevision, seed.Glossary.Id, 1,
                seed.Glossary.SourceVersion, seed.Glossary.DeletionGeneration),
                GroupBrainPayloadCodec.EncodeGlossary(term, "Published glossary definition"),
                Enumerable.Repeat((byte)0x32, 32).ToArray(), "owned");
            seed.GlossaryRevision.EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(seed.GlossaryRevision.ProtectedContent));
        }
        await f.Auth.Db.SaveChangesAsync();
        return new[] { first.Glossary.Id, second.Glossary.Id }.OrderByDescending(x => x).ToArray();
    }

    [Fact]
    public async Task InputPlanRoundTripRetainsOriginalChunkOrderAndAttemptIdentitiesWithoutEffects()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await f.CommitAsync(f.Payload($"plan-{i}", $"plan-event-{i}", "Tồn kho 😀 " + new string('x', 3000)))).MessageId);
        var claim = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var first = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [ids[0]]));
        var budget = GroupAutomaticModelContext.Create(first, brain, "existing-model", 64000).Manifest.UsedTokens;
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "existing-model", budget, []);
        Assert.Equal(3, plan.Chunks.Count);
        Assert.Equal(ids, plan.Chunks.SelectMany(x => x.Members).Select(x => x.MessageId));
        Assert.Equal(new[] { 1, 2, 3 }, plan.Chunks.Select(x => x.Ordinal));
        Assert.All(plan.Chunks, x => { Assert.True(x.RequiresModel); Assert.NotEqual(Guid.Empty, x.OperationId); Assert.Single(x.Members); });
        Assert.Equal(3, plan.Chunks.Select(x => x.OperationId).Distinct().Count());
        var partition = GroupAutomaticContextChunks.Create(preparation, brain, "existing-model", budget);
        for (var i = 0; i < plan.Chunks.Count; i++)
            Assert.Equal(GroupModelAttemptBudget.InputIdentity(partition[i].ModelContext!), plan.Chunks[i].InputSha256);
        var wire = plan.Encode(); var restored = GroupAutomaticInputPlan.Read(wire);
        restored.RequireRestored(preparation, brain, PlanProfile, []);
        Assert.Equal(wire, restored.Encode()); Assert.Equal(plan.Sha256, restored.Sha256);
        Assert.Equal(plan.Chunks.Select(x => x.OperationId), restored.Chunks.Select(x => x.OperationId));
        wire[0] ^= 1; Assert.Equal(plan.Encode(), restored.Encode());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<GroupAutomaticInputPlan.Chunk>>(plan.Chunks).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<GroupAutomaticInputPlan.Member>>(plan.Chunks[0].Members).Clear());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.DoesNotContain("Tồn kho", Encoding.UTF8.GetString(plan.Encode()));
        Assert.DoesNotContain("existing-model", plan.ToString());
    }

    [Fact]
    public async Task InputPlanHostOnlyRetainsQuarantineReceiptsAndHasNoModelOrPrivateText()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: "password=PRIVATE_PLAN_SECRET_6721"));
        var claim = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, null, 0, []);
        Assert.False(Assert.Single(plan.Chunks).RequiresModel);
        Assert.Equal(source.MessageId, Assert.Single(plan.Chunks[0].Members).MessageId);
        GroupAutomaticInputPlan.Read(plan.Encode()).RequireRestored(preparation, brain, PlanProfile, []);
        Assert.DoesNotContain("PRIVATE_PLAN_SECRET", Encoding.UTF8.GetString(plan.Encode()));
    }

    [Fact]
    public async Task InputPlanClosedCodecRejectsEveryTruncationChangedByteAndTrailingData()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var wire = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "existing-model", 64000, []).Encode();
        for (var i = 0; i < wire.Length; i++)
        {
            var truncated = wire.AsSpan(0, i).ToArray();
            Refuses(() => GroupAutomaticInputPlan.Read(truncated));
            var changed = wire.ToArray(); changed[i] ^= 1;
            Refuses(() => GroupAutomaticInputPlan.Read(changed));
        }
        Refuses(() => GroupAutomaticInputPlan.Read([.. wire, 0]));
        Refuses(() => GroupAutomaticInputPlan.Read(null));
        Refuses(() => GroupAutomaticInputPlan.Read(new byte[GroupAutomaticInputPlan.MaximumBytes + 1]));
        Assert.Equal(wire, GroupAutomaticInputPlan.Read(wire).Encode());

        static void Refuses(Action action)
        {
            var error = Assert.Throws<InvalidOperationException>(action);
            Assert.Equal("Group original input plan is not available.", error.Message); Assert.Null(error.InnerException);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("PRIVATE_CONFIG_TOKEN")]
    public async Task InputPlanRefusesInvalidProfileFingerprintWithoutEchoingConfiguration(string profile)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Create(preparation, brain, profile, "model", 64000, []));
        Assert.Equal("Group original input plan is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task InputPlanRestoreRejectsChangedProfileAndInputEvenWhenMetadataLooksUnchanged()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var original = await f.Reader.ReadAsync(claim, [source.MessageId]); var preparation = GroupBatchSourcePreparation.Create(original);
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "existing-model", 64000, []);
        Assert.Throws<InvalidOperationException>(() => plan.RequireRestored(preparation, brain, new string('B', 64), []));
        var changed = new GroupBatchSourceContext(claim, original.AllocatedThroughSequence, original.Coverage,
            original.Snapshots.ToArray(), [new(original.Snapshots[0], "different exact original model input")]);
        Assert.Throws<InvalidOperationException>(() => plan.RequireRestored(GroupBatchSourcePreparation.Create(changed), brain, PlanProfile, []));
        var changedProfilePlan = GroupAutomaticInputPlan.Create(preparation, brain, new string('B', 64), "existing-model", 64000, []);
        Assert.NotEqual(plan.Sha256, changedProfilePlan.Sha256);
        Assert.NotEqual(plan.Chunks[0].OperationId, changedProfilePlan.Chunks[0].OperationId);
    }

    [Fact]
    public async Task InputPlanRestoreRequiresOriginalGlossaryAndRejectsEmptyHistoryFallback()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var all = await ModelBrainAsync(f, claim, source, preparation);
        // Only the already read published glossary belongs in this positive
        // initial plan. The seeded request belongs to this same batch.
        var glossary = new GroupBrainPrivateContext(claim,
            all.Snapshots.Where(x => x.Kind == GroupBrainContentKind.GlossaryRevision).ToArray(),
            all.Items.Where(x => x.Kind == GroupBrainContentKind.GlossaryRevision).ToArray());
        var plan = GroupAutomaticInputPlan.Create(preparation, glossary, PlanProfile, "existing-model", 64000, []);
        var original = Assert.Single(plan.Dependencies.Dependencies);
        Assert.Equal(glossary.Items[0].RecordId, original.RecordId); Assert.Equal(glossary.Items[0].Revision, original.Revision);
        GroupAutomaticInputPlan.Read(plan.Encode()).RequireRestored(preparation, glossary, PlanProfile, []);
        var empty = await BrainReader(f).ReadAsync(claim, [], []);
        Assert.Throws<InvalidOperationException>(() => plan.RequireRestored(preparation, empty, PlanProfile, []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputPlanRejectsOwnBatchRequestAndMissingOriginInsteadOfSilentlyDroppingHistory(bool provideOrigin)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await ModelBrainAsync(f, claim, source, preparation);
        var id = brain.Items.Single(x => x.Kind == GroupBrainContentKind.RequestRevision).RecordId;
        GroupBrainRequestOriginMetadata[] origins = provideOrigin ? [new(f.Auth.Scope, id, claim.Receipt.BatchId, Guid.NewGuid(), 1, f.Auth.Clock.Current)] : [];
        var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "existing-model", 64000, origins));
        Assert.Equal("Group original input plan is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task InputPlanCancellationAndReboundContextNeverConstructReleaseAuthority()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, [], canceled.Token));
        var rebound = new GroupBrainPrivateContext(new(claim.Receipt, claim.Authority), [], []);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Create(preparation, rebound, PlanProfile, "model", 64000, []));
    }

    [Fact]
    public async Task InputPlanOriginalIdentitySurvivesClaimReplacementButNotMissingSourceOrChangedBudget()
    {
        using var f = new Fixture(); var first = await f.CommitAsync();
        var second = await f.CommitAsync(f.Payload("plan-second", "plan-second-event", "Nhập xuất"));
        var claim = await f.ClaimAsync(); var original = await f.Reader.ReadAsync(claim, [first.MessageId, second.MessageId]);
        var preparation = GroupBatchSourcePreparation.Create(original); var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []);
        // Structural capability replacement only: this does not claim that
        // these synthetic replacement fields have live SQL authority.
        var replacement = new GroupBatchClaimHandle(claim.Receipt with
        {
            Epoch = claim.Receipt.Epoch + 1,
            OperationId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid()
        }, claim.Authority);
        var rebound = GroupBatchSourcePreparation.Create(new(replacement, original.AllocatedThroughSequence, original.Coverage,
            original.Snapshots.ToArray(), original.Items.ToArray()));
        var reboundBrain = new GroupBrainPrivateContext(replacement, [], []);
        plan.RequireRestored(rebound, reboundBrain, PlanProfile, []);
        Assert.Equal(plan.Encode(), GroupAutomaticInputPlan.Create(rebound, reboundBrain, PlanProfile, "model", 64000, []).Encode());
        var missing = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [first.MessageId]));
        Assert.Throws<InvalidOperationException>(() => plan.RequireRestored(missing, brain, PlanProfile, []));
        var changed = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 63999, []);
        Assert.NotEqual(plan.Sha256, changed.Sha256);
        Assert.NotEqual(plan.Chunks[0].OperationId, changed.Chunks[0].OperationId);
    }
}
