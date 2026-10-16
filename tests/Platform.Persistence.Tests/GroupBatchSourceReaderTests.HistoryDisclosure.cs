using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    // These deterministic synthetic carriers establish byte witnesses only;
    // they grant no SQL authenticity, permissions or provider release.
    private static async Task<(GroupBatchSourcePreparation Preparation, GroupBrainPrivateContext Brain)> DisclosureGoldenData(int variant)
    {
        static Guid Id(int number) => new(number, 0, 0, new byte[8]);
        var scope = new GroupScope(Id(1), Id(2), Id(3));
        var service = Id(4); var account = Id(5); var batch = Id(6);
        var now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var authority = new GroupExtractionAuthority(new(scope.TenantId, scope.CompanyId, service, 1, true),
            new(scope.TenantId, scope.CompanyId, service, scope.SourceBindingId, GroupServiceCapability.Extract, 1, true),
            new(scope, account, new("synthetic", "owned-account", "owned-group"), "Owned source", 1, 0, true),
            1, "secretref://env/OWNED_GROUP_KEY");
        var handle = new GroupBatchClaimHandle(new(scope, Id(7), batch, Id(8), 1, TimeSpan.FromMinutes(5).Ticks,
            now, now.AddMinutes(5), service, 1, 1, 1, 0, 1), authority);
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var coverage = await GroupBatchCoverageSnapshot.ReadAsync(db, scope, account, CancellationToken.None);
        var texts = variant switch
        {
            0 => new[] { "Tra cứu tồn kho 😀\uFEFF \t \"literal\"", "Bổ sung yêu cầu" },
            1 => new[] { new string('x', 2500), new string('y', 2500), new string('z', 2500) },
            _ => new[] { "password=OWNED_SYNTHETIC_QUARANTINE" }
        };
        var snapshots = texts.Select((text, index) =>
        {
            var message = Id(20 + index); var eventId = "owned-event-" + index;
            var bytes = Encoding.UTF8.GetBytes(text);
            var head = new GroupBatchSourceHead(scope.TenantId, scope.CompanyId, scope.SourceBindingId, message,
                1, index + 1, GroupSourceEventKind.NewText, Convert.ToHexString(SHA256.HashData(bytes)), "owned", 1, 0,
                now, now, false, Encoding.UTF8.GetBytes(eventId), "owned-sender"u8.ToArray(), null, true,
                eventId, "owned-sender", null);
            var receipt = new GroupBatchSourceReceipt(scope.TenantId, scope.CompanyId, scope.SourceBindingId,
                new string('A', 64), message, 1, new string('B', 64), service, 1, 1, now, head.EventBytes, eventId);
            return new GroupBatchSourceSnapshot(head, head, "owned-message-" + index, "owned-sender", null,
                eventId, receipt, GroupBatchSourceDisposition.Readable, [1, 2, 3]);
        }).ToArray();
        var context = new GroupBatchSourceContext(handle, texts.Length, coverage, snapshots,
            snapshots.Select((snapshot, index) => new GroupBatchSourceEntry(snapshot, texts[index])).ToArray());
        var brainSnapshots = variant == 0 ? new[] { 42, 41 }.Select(number =>
        {
            var id = Id(number);
            var revision = new GroupBrainStoredRevision(scope.TenantId, scope.CompanyId, scope.SourceBindingId, id, 1,
                GroupRequestRevisionOrigin.ItEdited, GroupRequestVerificationLevel.ItConfirmed, null, Id(50), null, null,
                1, 0, "owned", new string('C', 64), now, [4, 5, 6]);
            return new GroupBrainSnapshot(GroupBrainContentKind.GlossaryRevision, id, null,
                new(1, 1, 1, 0, true, true, Id(50), now), revision, [], []);
        }).ToArray() : [];
        var brain = new GroupBrainPrivateContext(handle, brainSnapshots, brainSnapshots.Select((snapshot, index) =>
            new GroupBrainPrivateRevision(snapshot, GroupBrainPayloadCodec.EncodeGlossary("term" + index, "Nội dung đã xuất bản 😀"))).ToArray());
        return (GroupBatchSourcePreparation.Create(context), brain);
    }

    [Fact]
    public async Task HistoryDisclosureLegacyGoldenWitnesses()
    {
        var witnesses = new List<object>();
        for (var variant = 0; variant < 3; variant++)
        {
            var (preparation, brain) = await DisclosureGoldenData(variant);
            var budget = variant == 2 ? 0 : 64000;
            if (variant == 1)
            {
                var single = GroupBatchSourcePreparation.Create(new(preparation.Context.Handle,
                    preparation.Context.AllocatedThroughSequence, preparation.Context.Coverage,
                    [preparation.Context.Snapshots[0]], [preparation.Context.Items[0]]));
                budget = GroupAutomaticModelContext.Create(single, brain, "owned-model", 64000).Manifest.UsedTokens;
            }
            var chunks = GroupAutomaticContextChunks.Create(preparation, brain, variant == 2 ? null : "owned-model", budget);
            var plan = GroupAutomaticInputPlan.Create(preparation, brain, new string('A', 64), variant == 2 ? null : "owned-model", budget, []);
            witnesses.Add(new
            {
                variant,
                budget,
                inputs = chunks.Select(x => x.ModelContext?.CreateGatewayRequest(new Guid(99, 0, 0, new byte[8])).Input).ToArray(),
                inputHashes = chunks.Select(x => x.ModelContext is null ? null : GroupModelAttemptBudget.InputIdentity(x.ModelContext)).ToArray(),
                usedTokens = chunks.Select(x => x.ModelContext?.Manifest.UsedTokens).ToArray(),
                operations = plan.Chunks.Select(x => x.OperationId).ToArray(),
                planBase64 = Convert.ToBase64String(plan.Encode()),
                planSha256 = plan.Sha256
            });
        }
        // Captured with unchanged qualified8a production source BEFORE this
        // checkpoint. Binds complete input/manifest cost/plan/operation bytes.
        Assert.Equal("F9ABB2A883829C2CE6CBF0AEBC776BEEFD117EEF80FAB5909CB2C617C3A1FD1B",
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(witnesses))));
    }

    private static async Task<(GroupBatchSourcePreparation Preparation, GroupBrainPrivateContext Brain,
        GroupAutomaticHistorySelection Selection)> IssuedDisclosure(Fixture f, string text = "Tra cứu tồn kho 😀",
        int messages = 1, int glossaries = 0)
    {
        var receipts = new List<GroupIngressCommittedReceipt>();
        for (var i = 0; i < messages; i++)
            receipts.Add(await f.CommitAsync(f.Payload("disclosure-" + i, "disclosure-event-" + i, text)));
        var handle = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, receipts.Select(x => x.MessageId).ToArray()));
        var first = GroupBatchSourcePreparation.Create(new(handle, preparation.Context.AllocatedThroughSequence,
            preparation.Context.Coverage, [preparation.Context.Snapshots[0]], [preparation.Context.Items[0]]));
        for (var i = 0; i < glossaries; i++) _ = await ModelBrainAsync(f, handle, receipts[0], first);
        var selection = await HistorySelector(f).SelectAsync(preparation);
        var brain = await BrainReader(f).ReadAsync(handle, selection.RequestIds, selection.GlossaryIds);
        return (preparation, brain, selection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryDisclosureFalseEmptyAndHintOverflowNeverClaimCompleteHistory(bool overflow)
    {
        using var f = new Fixture();
        var text = overflow ? string.Join(" ", Enumerable.Range(1, 101).Select(HistoryCode)) : "Yêu cầu kiểm thử";
        var (preparation, brain, selection) = await IssuedDisclosure(f, text);
        Assert.Empty(brain.Items); Assert.Equal(overflow, selection.HasAdditionalHistory);
        var context = GroupAutomaticModelContext.CreateV2(preparation, brain, selection, "model", 64000);
        using var json = JsonDocument.Parse(context.CreateGatewayRequest(Guid.NewGuid()).Input);
        Assert.Equal("group-automatic-context-v2", json.RootElement.GetProperty("version").GetString());
        var disclosure = json.RootElement.GetProperty("history_selection");
        Assert.Equal("not_asserted", disclosure.GetProperty("completeness").GetString());
        Assert.Equal(overflow, disclosure.GetProperty("selection_may_be_incomplete").GetBoolean());
        Assert.Equal(0, disclosure.GetProperty("selected_request_revisions").GetInt32());
        Assert.Equal(0, disclosure.GetProperty("selected_glossary_revisions").GetInt32());
        Assert.Contains("Unlisted work may exist", json.RootElement.GetProperty("policy").GetString());
        Assert.Contains("does not prove extra matching records exist", json.RootElement.GetProperty("policy").GetString());
        Assert.True(context.RequiresModelTokenizerQualification);
        Assert.DoesNotContain(f.Auth.Scope.TenantId.ToString("D"), context.CreateGatewayRequest(Guid.NewGuid()).Input);
        Assert.DoesNotContain("disclosure-event", context.ToString());
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("copy")]
    [InlineData("captured-data")]
    public async Task HistoryDisclosureScopeOnlyMetadataCannotManufactureSelectorIssuance(string kind)
    {
        using var f = new Fixture(); var (preparation, brain, issued) = await IssuedDisclosure(f);
        var metadata = kind == "rank" ? GroupAutomaticHistorySelector.Rank(preparation.Scope, [], [], [], [], false)
            : new GroupAutomaticHistorySelection(issued.Scope, issued.RequestIds.ToArray(), issued.GlossaryIds.ToArray(), issued.HasAdditionalHistory);
        if (kind == "captured-data") _ = GroupHistorySelectionDisclosure.Capture(preparation, metadata);
        var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticModelContext.CreateV2(preparation, brain, metadata, "model", 64000));
        Assert.Equal("Group automatic model context is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticContextChunks.CreateV2(preparation, brain, metadata, "model", 64000));
    }

    [Theory]
    [InlineData("rebound-handle")]
    [InlineData("text")]
    [InlineData("cutoff")]
    [InlineData("extra-source")]
    [InlineData("cipher")]
    public async Task HistoryDisclosureBindsOriginalHandleWholeSourcesAndPrivateInput(string kind)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = preparation.Context;
        var handle = kind == "rebound-handle" ? new GroupBatchClaimHandle(original.Handle.Receipt, original.Handle.Authority) : original.Handle;
        var snapshots = original.Snapshots.ToArray(); var items = original.Items.ToArray();
        if (kind == "text") items[0] = new(snapshots[0], "different private text");
        if (kind == "extra-source")
        {
            var head = snapshots[0].Head with { MessageId = Guid.NewGuid() };
            snapshots[0] = snapshots[0] with { Head = head, CurrentHead = head };
            items[0] = new(snapshots[0], items[0].Text);
        }
        if (kind == "cipher") snapshots[0].ProtectedContent![0] ^= 1;
        var changed = GroupBatchSourcePreparation.Create(new(handle, original.AllocatedThroughSequence + (kind == "cutoff" ? 1 : 0),
            original.Coverage, snapshots, items));
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticModelContext.CreateV2(changed, brain, selection, "model", 64000));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("reordered")]
    [InlineData("snapshot-pair")]
    [InlineData("foreign-handle")]
    public async Task HistoryDisclosureRequiresExactDerivedHistoryCountsAndOriginalOrder(string kind)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, glossaries: 2);
        Assert.Equal(2, brain.Items.Count);
        var snapshots = brain.Snapshots.ToArray(); var items = brain.Items.ToArray(); var handle = brain.Handle;
        switch (kind)
        {
            case "missing": snapshots = snapshots.Take(1).ToArray(); items = items.Take(1).ToArray(); break;
            case "reordered": snapshots = snapshots.Reverse().ToArray(); items = items.Reverse().ToArray(); break;
            case "snapshot-pair": snapshots[0] = snapshots[0] with { RecordId = Guid.NewGuid() }; break;
            default: handle = new(handle.Receipt, handle.Authority); break;
        }
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticModelContext.CreateV2(preparation, new(handle, snapshots, items), selection, "model", 64000));
    }

    [Fact]
    public async Task HistoryDisclosureDerivesFrozenCountsAndCopiesSelectedOrderWithoutRawScopeInPrompt()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, glossaries: 2);
        var disclosure = GroupAutomaticHistorySelector.RequireDisclosure(selection, preparation, brain, false, CancellationToken.None);
        Assert.Equal(0, disclosure.RequestCount); Assert.Equal(2, disclosure.GlossaryCount);
        Assert.Equal(brain.Items.Select(x => x.RecordId), disclosure.SelectedOrder.Select(x => x.RecordId));
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<GroupHistorySelectionDisclosure.HistoryMember>>(disclosure.SelectedOrder).Clear());
        var request = GroupAutomaticModelContext.CreateV2(preparation, brain, selection, "model", 64000).CreateGatewayRequest(Guid.NewGuid());
        using var json = JsonDocument.Parse(request.Input);
        Assert.Equal(2, json.RootElement.GetProperty("same_group_history").GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("history_selection").GetProperty("selected_glossary_revisions").GetInt32());
        foreach (var id in new[] { preparation.Scope.TenantId, preparation.Scope.CompanyId, preparation.Scope.SourceBindingId }) Assert.DoesNotContain(id.ToString("D"), request.Input);
        Assert.DoesNotContain(brain.Items[0].Content, disclosure.ToString());
    }

    [Fact]
    public void HistoryDisclosureMetadataCollectionsAreDefensiveCopiesEvenWhenNotIssued()
    {
        var requests = new[] { Guid.NewGuid() }; var glossary = new[] { Guid.NewGuid() };
        var selected = new GroupAutomaticHistorySelection(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), requests, glossary, false);
        var request = requests[0]; var entry = glossary[0]; requests[0] = Guid.NewGuid(); glossary[0] = Guid.NewGuid();
        Assert.Equal(request, selected.RequestIds[0]); Assert.Equal(entry, selected.GlossaryIds[0]);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<Guid>>(selected.RequestIds).Clear());
    }

    [Fact]
    public async Task HistoryDisclosureEveryV2ChunkCarriesIdenticalRequiredMetadataAndExactAccounting()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, new string('x', 3000), messages: 3, glossaries: 1);
        var original = preparation.Context;
        var first = GroupBatchSourcePreparation.Create(new(original.Handle, original.AllocatedThroughSequence, original.Coverage,
            [original.Snapshots[0]], [original.Items[0]]));
        var singleton = GroupAutomaticModelContext.CreateV2(first, brain, selection, "model", 64000);
        var budget = singleton.Manifest.UsedTokens;
        var chunks = GroupAutomaticContextChunks.CreateV2(preparation, brain, selection, "model", budget);
        Assert.Equal(3, chunks.Count); string? metadata = null;
        foreach (var chunk in chunks)
        {
            Assert.NotNull(chunk.HistoryDisclosure); Assert.NotNull(chunk.ModelContext);
            var request = chunk.ModelContext!.CreateGatewayRequest(Guid.NewGuid());
            using var json = JsonDocument.Parse(request.Input);
            var current = json.RootElement.GetProperty("history_selection").GetRawText(); metadata ??= current; Assert.Equal(metadata, current);
            Assert.Equal(Encoding.UTF8.GetByteCount(request.Input) + Encoding.UTF8.GetByteCount(request.ResponseSchema!), chunk.ModelContext.Manifest.UsedTokens);
            Assert.Equal(budget, chunk.ModelContext.Manifest.UsedTokens);
            Assert.Equal(1 + brain.Items.Count + 3, chunk.ModelContext.Manifest.Entries.Count);
            Assert.Contains(chunk.ModelContext.Manifest.Entries, entry => entry.SourceId == "policy:group-automatic-context-v2");
            Assert.Contains(chunk.ModelContext.Manifest.Entries, entry => entry.SourceId == "framing:group-automatic-context-v2");
        }
        Assert.Equal(preparation.Receipts, chunks.SelectMany(x => x.Preparation.Receipts));
        var exact = GroupAutomaticModelContext.CreateV2(first, brain, selection, "model", budget);
        Assert.Equal(singleton.CreateGatewayRequest(Guid.NewGuid()).Input, exact.CreateGatewayRequest(Guid.NewGuid()).Input);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticModelContext.CreateV2(first, brain, selection, "model", budget - 1));
        var legacyBudget = GroupAutomaticModelContext.Create(first, brain, "model", 64000).Manifest.UsedTokens;
        Assert.True(budget > legacyBudget);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticContextChunks.CreateV2(preparation, brain, selection, "model", legacyBudget));
        Assert.Equal("group-automatic-context-v1", GroupAutomaticModelContext.Create(first, brain, "model", legacyBudget).InputFormatVersion);
    }

    [Fact]
    public async Task HistoryDisclosureLargeWholeInputRefusesRatherThanTruncatesWhileChunksRetainAllSources()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, new string('x', 8000), messages: 9);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticModelContext.CreateV2(preparation, brain, selection, "model", 64000));
        var chunks = GroupAutomaticContextChunks.CreateV2(preparation, brain, selection, "model", 64000);
        Assert.True(chunks.Count > 1); Assert.Equal(9, chunks.Sum(x => x.Preparation.Candidates.Count));
        Assert.Equal(preparation.Receipts, chunks.SelectMany(x => x.Preparation.Receipts));
        Assert.All(chunks, chunk => Assert.InRange(chunk.ModelContext!.Manifest.UsedTokens, 1, 64000));
    }

    [Fact]
    public async Task HistoryDisclosureHostOnlyRetainsMetadataWithoutConstructingAProviderInput()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, "password=OWNED_SYNTHETIC_QUARANTINE");
        Assert.Empty(preparation.Candidates);
        var chunk = Assert.Single(GroupAutomaticContextChunks.CreateV2(preparation, brain, selection, null, 0));
        Assert.Null(chunk.ModelContext); Assert.NotNull(chunk.HistoryDisclosure);
        Assert.Equal(preparation.Receipts, chunk.Preparation.Receipts);
    }

    [Fact]
    public async Task HistoryDisclosureIsNotACachedCurrentPermissionVerdictAndCancellationCannotReleaseInput()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupAutomaticModelContext.CreateV2(preparation, brain, selection, "model", 64000, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => GroupAutomaticContextChunks.CreateV2(preparation, brain, selection, "model", 64000, cancelled.Token));
        f.Extract.IsEnabled = false; await f.Auth.Db.SaveChangesAsync();
        // Pure original metadata still exists; actual release requires the
        // unchanged live source/brain fences and must refuse this revocation.
        Assert.True(GroupAutomaticModelContext.CreateV2(preparation, brain, selection, "model", 64000).RequiresModelTokenizerQualification);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reader.RequireCurrentAsync(preparation.Context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrainReader(f).RequireCurrentAsync(brain));
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }
}
