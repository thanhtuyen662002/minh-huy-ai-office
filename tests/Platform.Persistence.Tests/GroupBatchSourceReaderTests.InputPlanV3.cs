using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Microsoft.EntityFrameworkCore;
using System.Data;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputPlanV3StoresIssuedConservativeDisclosureAndExactOriginalInput(bool limited)
    {
        using var f = new Fixture();
        var text = limited ? string.Join(" ", Enumerable.Range(1, 101).Select(HistoryCode)) : "Yêu cầu tồn kho 😀";
        var (preparation, brain, selection) = await IssuedDisclosure(f, text);
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        plan.RequireOriginalIssuance(preparation, brain, []);
        var bytes = plan.Encode(); var decoded = GroupAutomaticInputPlan.Read(bytes);
        Assert.Equal(3, decoded.WireFormatVersion); Assert.Equal("AIOGPLN3", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal(bytes, decoded.Encode()); Assert.Equal(plan.Sha256, decoded.Sha256);
        var restored = Assert.Single(decoded.RestoreChunks(preparation, brain, PlanProfile, []));
        var current = GroupAutomaticModelContext.CreateV2(preparation, brain, selection, "model", 64000);
        Assert.Equal(current.CreateGatewayRequest(Guid.NewGuid()).Input, restored.Current.ModelContext!.CreateGatewayRequest(Guid.NewGuid()).Input);
        using var json = JsonDocument.Parse(restored.Current.ModelContext.CreateGatewayRequest(Guid.NewGuid()).Input);
        Assert.Equal(limited, json.RootElement.GetProperty("history_selection").GetProperty("selection_may_be_incomplete").GetBoolean());
        Assert.Equal("not_asserted", json.RootElement.GetProperty("history_selection").GetProperty("completeness").GetString());
        Assert.Equal(decoded.Chunks[0].InputSha256, GroupModelAttemptBudget.InputIdentity(restored.Current.ModelContext));
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.DoesNotContain(text, Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("copy")]
    [InlineData("capture")]
    public async Task InputPlanV3RefusesUnissuedFirstOriginalCapabilities(string kind)
    {
        using var f = new Fixture(); var (preparation, brain, issued) = await IssuedDisclosure(f);
        var selection = kind == "rank" ? GroupAutomaticHistorySelector.Rank(preparation.Scope, [], [], [], [], false)
            : new GroupAutomaticHistorySelection(issued.Scope, issued.RequestIds.ToArray(), issued.GlossaryIds.ToArray(), issued.HasAdditionalHistory);
        if (kind == "capture") _ = GroupHistorySelectionDisclosure.Capture(preparation, selection);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []));
    }

    [Fact]
    public async Task InputPlanV3DecodedRestorationNeverManufacturesNewIssuance()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var decoded = GroupAutomaticInputPlan.Read(original.Encode());
        var error = Assert.Throws<InvalidOperationException>(() => decoded.RequireOriginalIssuance(preparation, brain, []));
        Assert.Equal("Group original input plan is not available.", error.Message); Assert.Null(error.InnerException);
        var restored = Assert.Single(decoded.RestoreChunks(preparation, brain, PlanProfile, []));
        var fake = new GroupAutomaticHistorySelection(preparation.Scope, [], [], false);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticModelContext.CreateV2(preparation, restored.Brain, fake, "model", 64000));
        decoded.RequireRestored(preparation, brain, PlanProfile, []);
        original.RequireOriginalIssuance(preparation, brain, []);
    }

    [Fact]
    public async Task InputPlanV3ReboundClaimAndFreshReorderedReaderRecoverOriginalWithoutSelection()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, glossaries: 2);
        var plan = GroupAutomaticInputPlan.Read(GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []).Encode());
        // Pure replacement metadata, not a live SQL authority claim.
        var handle = new GroupBatchClaimHandle(preparation.Context.Handle.Receipt with
        { Epoch = preparation.Context.Handle.Receipt.Epoch + 1, OperationId = Guid.NewGuid(), OwnerId = Guid.NewGuid() },
            preparation.Context.Handle.Authority);
        var current = GroupBatchSourcePreparation.Create(new(handle, preparation.Context.AllocatedThroughSequence,
            preparation.Context.Coverage, preparation.Context.Snapshots.ToArray(), preparation.Context.Items.ToArray()));
        var grouped = new GroupBrainPrivateContext(handle, brain.Snapshots.Reverse().ToArray(), brain.Items.Reverse().ToArray());
        var restored = Assert.Single(plan.RestoreChunks(current, grouped, PlanProfile, []));
        Assert.Equal(plan.HistoryOrder.Select(x => x.RecordId), restored.Brain.Items.Select(x => x.RecordId));
        Assert.Same(handle, restored.Brain.Handle);
        Assert.Equal(plan.Chunks[0].InputSha256, GroupModelAttemptBudget.InputIdentity(restored.Current.ModelContext!));
        Assert.Throws<InvalidOperationException>(() => plan.RequireOriginalIssuance(current, restored.Brain, []));
        // No selector was invoked for this fresh process-style reconstruction.
        Assert.Equal(brain.Items.Reverse().Select(x => x.RecordId), grouped.Items.Select(x => x.RecordId));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("cutoff")]
    [InlineData("cipher")]
    [InlineData("profile")]
    [InlineData("missing-history")]
    [InlineData("reordered-history")]
    public async Task InputPlanV3RecoveryRefusesChangedOriginalInputsAndSelection(string kind)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, glossaries: 2);
        var plan = GroupAutomaticInputPlan.Read(GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []).Encode());
        var original = preparation.Context; var items = original.Items.ToArray(); var snapshots = original.Snapshots.ToArray();
        if (kind == "text") items[0] = new(snapshots[0], "different private input");
        if (kind == "cipher") snapshots[0].ProtectedContent![0] ^= 1;
        var changed = GroupBatchSourcePreparation.Create(new(original.Handle,
            original.AllocatedThroughSequence + (kind == "cutoff" ? 1 : 0), original.Coverage, snapshots, items));
        var history = kind == "missing-history" ? new GroupBrainPrivateContext(brain.Handle, [], [])
            : kind == "reordered-history" ? new GroupBrainPrivateContext(brain.Handle, brain.Snapshots.Reverse().ToArray(), brain.Items.Reverse().ToArray()) : brain;
        Assert.Throws<InvalidOperationException>(() => plan.RequireRestored(changed, history, kind == "profile" ? new string('B', 64) : PlanProfile, []));
    }

    [Fact]
    public async Task InputPlanV3OriginalWireAndDisclosureExportsAreDefensiveCopies()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var wire = plan.Encode(); var disclosure = plan.OriginalDisclosureBytes!; disclosure[^1] ^= 1;
        var read = GroupAutomaticInputPlan.Read(wire); wire[0] ^= 1;
        Assert.Equal(plan.Encode(), read.Encode()); Assert.NotEqual(disclosure, plan.OriginalDisclosureBytes);
        read.RequireRestored(preparation, brain, PlanProfile, []);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<GroupAutomaticInputPlan.HistoryMember>>(read.HistoryOrder).Clear());
    }

    [Fact]
    public async Task InputPlanV3ClosedCodecRefusesEveryChangedByteTruncationAndUnsupportedVersion()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var wire = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []).Encode();
        for (var i = 0; i < wire.Length; i++)
        {
            Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Read(wire[..i]));
            var changed = wire.ToArray(); changed[i] ^= 1;
            Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Read(changed));
        }
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Read([.. wire, 0]));
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Read(new byte[32001]));
        foreach (var version in new[] { (byte)'1', (byte)'4' })
        { var changed = wire.ToArray(); changed[7] = version; Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Read(changed)); }
    }

    [Fact]
    public async Task InputPlanV3HostOnlyBindsDisclosureWithoutProviderInputAndRetainsLegacyIdentity()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, "password=OWNED_V3_CANARY");
        var legacy = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, null, 0, []);
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, null, 0, []);
        Assert.False(Assert.Single(plan.Chunks).RequiresModel); Assert.NotNull(plan.OriginalDisclosureBytes);
        Assert.NotEqual(legacy.Chunks[0].InputSha256, plan.Chunks[0].InputSha256);
        Assert.NotEqual(legacy.Chunks[0].OperationId, plan.Chunks[0].OperationId);
        var restored = Assert.Single(GroupAutomaticInputPlan.Read(plan.Encode()).RestoreChunks(preparation, brain, PlanProfile, []));
        Assert.Null(restored.Current.ModelContext); Assert.NotNull(restored.Current.HistoryDisclosure);
        Assert.Equal(preparation.Receipts, restored.Current.Preparation.Receipts);
        Assert.Equal(2, legacy.WireFormatVersion); Assert.Null(legacy.OriginalDisclosureBytes);
        Assert.DoesNotContain("OWNED_V3_CANARY", Encoding.UTF8.GetString(plan.Encode()));
    }

    [Fact]
    public async Task InputPlanV3PartitionRestoresEveryOriginalChunkWithExactBudgetAndOperations()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, new string('x', 3000), 3, 1);
        var original = preparation.Context;
        var first = GroupBatchSourcePreparation.Create(new(original.Handle, original.AllocatedThroughSequence,
            original.Coverage, [original.Snapshots[0]], [original.Items[0]]));
        var budget = GroupAutomaticModelContext.CreateV2(first, brain, selection, "model", 64000).Manifest.UsedTokens;
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", budget, []);
        Assert.Equal(3, plan.Chunks.Count);
        var recovered = GroupAutomaticInputPlan.Read(plan.Encode()).RestoreChunks(preparation, brain, PlanProfile, []);
        Assert.Equal(plan.Chunks.Select(x => x.OperationId), recovered.Select(x => x.Original.OperationId));
        Assert.Equal(preparation.Receipts, recovered.SelectMany(x => x.Current.Preparation.Receipts));
        Assert.All(recovered, x =>
        {
            Assert.Equal(budget, x.Current.ModelContext!.Manifest.UsedTokens);
            Assert.Equal(x.Original.InputSha256, GroupModelAttemptBudget.InputIdentity(x.Current.ModelContext));
            Assert.NotNull(x.Current.HistoryDisclosure);
        });
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", budget - 1, []));
    }

    [Fact]
    public async Task InputPlanV3OversizeWholeInputStillPartitionsWithoutTruncation()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, new string('x', 8000), 9);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticModelContext.CreateV2(preparation, brain, selection, "model", 64000));
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var recovered = GroupAutomaticInputPlan.Read(plan.Encode()).RestoreChunks(preparation, brain, PlanProfile, []);
        Assert.True(recovered.Count > 1); Assert.Equal(9, recovered.Sum(x => x.Original.Members.Count));
        Assert.All(recovered, x => Assert.InRange(x.Current.ModelContext!.Manifest.UsedTokens, 1, 64000));
    }

    [Fact]
    public async Task InputPlanV3CancellationRefusesBothCreationAndRecovery()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, [], cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => plan.RestoreChunks(preparation, brain, PlanProfile, [], cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => plan.RequireOriginalIssuance(preparation, brain, [], cancelled.Token));
    }

    [Fact]
    public void InputPlanV3ActualMaximumShapeRoundTripsBelowConservative24105Bound()
    {
        Assert.Equal(18499, GroupAutomaticInputPlan.MaximumV2EncodedBytes);
        Assert.Equal(5604, GroupHistorySelectionDisclosure.MaximumOriginalBytes);
        Assert.Equal(24105, GroupAutomaticInputPlan.MaximumV3EncodedBytes);
        var wire = MaximumV3Wire();
        var plan = GroupAutomaticInputPlan.Read(wire);
        Assert.Equal(wire, plan.Encode()); Assert.Equal(100, plan.Dependencies.Sources.Count);
        Assert.Equal(20, plan.HistoryOrder.Count); Assert.Equal(100, plan.Chunks.Count);
        Assert.Equal(100, plan.Chunks.Sum(x => x.Members.Count));
        Assert.InRange(wire.Length, 20000, 24105);
        // This forged, canonical pure wire is not first-insert authority.
        Assert.Throws<InvalidOperationException>(() => plan.RequireOriginalIssuance(null!, null!, []));
    }

    [Theory]
    [InlineData(2, 20, 100)]
    [InlineData(1, 21, 100)]
    [InlineData(1, 20, 99)]
    [InlineData(1, 0, 100)]
    public void InputPlanV3SelfConsistentSeedsCannotAuthorizeMalformedClosedDisclosure(byte flag, int limit, byte sourceCount)
    {
        // The operation seed is recomputed for the forged wire. Its closed
        // disclosure must refuse independently of a mere checksum mismatch.
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlan.Read(MaximumV3Wire(flag, limit, sourceCount)));
    }

    [Fact]
    public void InputPlanV3ChangingOnlyConservativeFlagChangesPlanAndAllOperationSeedsWithoutGrantingIssuance()
    {
        var limited = GroupAutomaticInputPlan.Read(MaximumV3Wire(1));
        var other = GroupAutomaticInputPlan.Read(MaximumV3Wire(0));
        Assert.Equal(limited.Dependencies.Sources, other.Dependencies.Sources);
        Assert.Equal(limited.HistoryOrder, other.HistoryOrder);
        Assert.NotEqual(limited.Sha256, other.Sha256);
        Assert.All(limited.Chunks.Zip(other.Chunks), pair => Assert.NotEqual(pair.First.OperationId, pair.Second.OperationId));
        Assert.Throws<InvalidOperationException>(() => limited.RequireOriginalIssuance(null!, null!, []));
        Assert.Throws<InvalidOperationException>(() => other.RequireOriginalIssuance(null!, null!, []));
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, false)]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, false)]
    [InlineData(0, 0, false)]
    [InlineData(1, 2, false)]
    [InlineData(2, 0, false)]
    [InlineData(99, 2, false)]
    [InlineData(2, 99, false)]
    public void InputPlanV3LegacyStoreSupportedFormatMatrixRemainsTwoOnly(int rowFormat, int codecFormat, bool accepted)
    {
        if (accepted) GroupAutomaticInputPlanStore.RequireSupportedFormat(rowFormat, codecFormat);
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlanStore.RequireSupportedFormat(rowFormat, codecFormat));
            Assert.Equal("Group original input plan store is not available.", error.Message); Assert.Null(error.InnerException);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputPlanV3ActualLegacyStoreRefusesIssuedAndDecodedThreeBeforeAnySql(bool decoded)
    {
        using var f = new Fixture(); var (preparation, currentBrain, selection) = await IssuedDisclosure(f);
        var issued = GroupAutomaticInputPlan.CreateV3(preparation, currentBrain, selection, PlanProfile, "model", 64000, []);
        issued.RequireOriginalIssuance(preparation, currentBrain, []);
        var plan = decoded ? GroupAutomaticInputPlan.Read(issued.Encode()) : issued;
        var trap = new ModelFenceConnectionTrap(); var keys = f.Keys.Reads;
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;MultipleActiveResultSets=false")
            .AddInterceptors(trap).Options);
        var sources = new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var brain = new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var store = new GroupAutomaticInputPlanStore(database, f.Worker, f.Auth.Clock, sources, brain);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistAsync(plan, preparation, currentBrain));
        Assert.Equal("Group original input plan store is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
        Assert.False(database.ChangeTracker.HasChanges());
    }

    private static byte[] MaximumV3Wire(byte flag = 1, int totalLimit = 20, byte disclosedSourceCount = 100)
    {
        var scope = new GroupScope(new(101, 0, 0, new byte[8]), new(102, 0, 0, new byte[8]), new(103, 0, 0, new byte[8]));
        var batch = new Guid(104, 0, 0, new byte[8]);
        var hash = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        var sources = Enumerable.Range(1, 100).Select(x => new Guid(x, 0, 0, new byte[8])).Order().ToArray();
        var origins = Enumerable.Range(1, 20).Select(x =>
        {
            var oldBatch = new Guid(1000 + x, 0, 0, new byte[8]); var operation = new Guid(2000 + x, 0, 0, new byte[8]);
            var id = new Guid(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
                $"aioffice-group-note-id-v1/{scope.TenantId:D}/{scope.CompanyId:D}/{scope.SourceBindingId:D}/{oldBatch:D}/{operation:D}/request/1"))).AsSpan(0, 16));
            return new GroupBrainRequestOriginMetadata(scope, id, oldBatch, operation, 1, DateTimeOffset.UnixEpoch);
        }).OrderBy(x => x.RequestId).ToArray();
        var utf8 = new UTF8Encoding(false, true);
        byte[] manifest;
        using (var stream = new MemoryStream())
        {
            using var w = new BinaryWriter(stream, utf8, true); w.Write("AIOGDEP1"u8);
            foreach (var id in new[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId, batch, batch }) w.Write(id.ToByteArray());
            w.Write(100L); w.Write(hash); w.Write(hash); w.Write((byte)100); w.Write((byte)20);
            foreach (var id in sources) { w.Write(id.ToByteArray()); w.Write(1L); w.Write(hash); }
            foreach (var row in origins) { w.Write((byte)GroupBrainContentKind.RequestRevision); w.Write(row.RequestId.ToByteArray()); w.Write(1L); w.Write(hash); }
            w.Flush(); manifest = stream.ToArray();
        }
        byte[] disclosure;
        using (var stream = new MemoryStream())
        {
            using var w = new BinaryWriter(stream, utf8, true); w.Write("AIOGHD01"u8);
            foreach (var text in new[] { "group-history-selection-v1", "same_authorized_group_current_generation_excluding_own_batch", "not_asserted" }) Text(w, text);
            foreach (var count in new[] { totalLimit, 4, 100, 21, 5 }) w.Write(count);
            w.Write(flag); w.Write(disclosedSourceCount);
            foreach (var id in sources) { w.Write(id.ToByteArray()); w.Write(hash); }
            w.Flush(); disclosure = stream.ToArray();
        }
        var seed = SHA256.HashData(Wire(null)); return Wire(seed);
        byte[] Wire(byte[]? seed)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, utf8, true); w.Write("AIOGPLN3"u8);
            foreach (var text in new[] { "group-automatic-context-v2", "utf8-byte-estimate-v1", "group-secret-quarantine-v1",
                "group-work-proposal-v1", "group-model-attempt-budget-v1", "32D0403401CE4E3498DEA3D2CC68B0EC275E0641DBD695E48248EBCA9EC8E4DE" }) Text(w, text);
            w.Write(manifest.Length); w.Write(manifest); w.Write((ushort)disclosure.Length); w.Write(disclosure);
            w.Write((byte)20); foreach (var row in origins) { w.Write((byte)GroupBrainContentKind.RequestRevision); w.Write(row.RequestId.ToByteArray()); }
            w.Write(hash); Text(w, new string('m', 256)); w.Write(64000); w.Write((byte)20);
            foreach (var row in origins) { w.Write(row.RequestId.ToByteArray()); w.Write(row.OriginBatchId.ToByteArray()); w.Write(row.OriginOperationId.ToByteArray()); w.Write(row.OriginCandidateOrdinal); w.Write(row.CreatedAtUtc.UtcTicks); }
            w.Write((byte)100);
            for (var i = 0; i < 100; i++)
            {
                var operation = Guid.Empty;
                if (seed is not null)
                {
                    using var opStream = new MemoryStream(); using var opWriter = new BinaryWriter(opStream, utf8, true);
                    opWriter.Write("aioffice-group-original-chunk-v1"); opWriter.Write(seed); opWriter.Write(i + 1); opWriter.Flush();
                    operation = new(SHA256.HashData(opStream.ToArray()).AsSpan(0, 16));
                }
                w.Write(operation.ToByteArray()); w.Write((byte)1); w.Write(hash); w.Write((byte)1);
                w.Write(sources[i].ToByteArray()); w.Write(1L); w.Write((long)i + 1);
            }
            w.Flush(); return stream.ToArray();
        }
        void Text(BinaryWriter writer, string text)
        { var bytes = utf8.GetBytes(text); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
    }
}
