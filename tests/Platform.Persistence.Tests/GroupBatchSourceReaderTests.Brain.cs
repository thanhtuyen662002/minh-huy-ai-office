using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static GroupBrainCurrentReader BrainReader(Fixture fixture) => new(fixture.Auth.Db, fixture.Worker,
        fixture.Auth.Clock, fixture.Keys, new());
    private sealed record BrainSeed(GroupCustomerRequestRecord Request, GroupRequestRevisionRecord Revision,
        GroupGlossaryEntryRecord Glossary, GroupGlossaryRevisionRecord GlossaryRevision);
    private static async Task<BrainSeed> SeedBrainAsync(Fixture f, GroupBatchClaimHandle claim,
        GroupIngressCommittedReceipt source, string content = "{\"private\":\"Tồn kho 😀�\uFEFF \"}", GroupScope? scopeOverride = null)
    {
        var scope = scopeOverride ?? f.Auth.Scope; var now = f.Auth.Clock.Current;
        var id = Guid.NewGuid(); var glossaryId = Guid.NewGuid(); var publisher = Guid.NewGuid();
        var request = new GroupCustomerRequestRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            Id = id,
            OriginBatchId = claim.Receipt.BatchId,
            OriginOperationId = Guid.NewGuid(),
            OriginCandidateOrdinal = 1,
            RequestCode = "REQ-" + id.ToString("N").ToUpperInvariant(),
            Kind = GroupNoteKind.Incident,
            SourceVersion = claim.Authority.Source.Version,
            DeletionGeneration = claim.Authority.Source.DeletionGeneration,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var key = Enumerable.Repeat((byte)0x32, 32).ToArray();
        var revision = new GroupRequestRevisionRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            RequestId = id,
            Revision = 1,
            Origin = GroupRequestRevisionOrigin.AiExtracted,
            VerificationLevel = GroupRequestVerificationLevel.SourceBackedAiInterpretation,
            AuthorServiceId = f.Worker.ServiceId,
            SourceBatchId = claim.Receipt.BatchId,
            ClaimEpoch = claim.Receipt.Epoch,
            SourceVersion = request.SourceVersion,
            DeletionGeneration = request.DeletionGeneration,
            ContentKeyId = "owned",
            CreatedAtUtc = now,
            ProtectedContent = new GroupBrainContentProtector().Protect(new(scope, GroupBrainContentKind.RequestRevision,
                id, 1, request.SourceVersion, request.DeletionGeneration), content, key, "owned")
        };
        revision.EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(revision.ProtectedContent));
        var glossary = new GroupGlossaryEntryRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            Id = glossaryId,
            CurrentRevision = 1,
            SourceVersion = request.SourceVersion,
            DeletionGeneration = request.DeletionGeneration,
            IsEnabled = true,
            AllowExtraction = true,
            PublishedByUserId = publisher,
            CreatedAtUtc = now
        };
        var glossaryRevision = new GroupGlossaryRevisionRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            EntryId = glossaryId,
            Revision = 1,
            SourceVersion = request.SourceVersion,
            DeletionGeneration = request.DeletionGeneration,
            PublishedByUserId = publisher,
            ContentKeyId = "owned",
            CreatedAtUtc = now,
            ProtectedContent = new GroupBrainContentProtector().Protect(new(scope, GroupBrainContentKind.GlossaryRevision,
                glossaryId, 1, request.SourceVersion, request.DeletionGeneration), "{\"term\":\"nhập–xuất 😀\"}", key, "owned")
        };
        glossaryRevision.EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(glossaryRevision.ProtectedContent));
        f.Auth.Db.AddRange(request, revision, glossary, glossaryRevision, new GroupRequestEvidenceRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            RequestId = id,
            RequestRevision = 1,
            Ordinal = 1,
            MessageId = source.MessageId,
            MessageRevision = source.Revision,
            Kind = GroupRequestEvidenceKind.LiteralSourceQuote
        });
        await f.Auth.Db.SaveChangesAsync(); return new(request, revision, glossary, glossaryRevision);
    }

    [Fact]
    public async Task BrainReadsExactCurrentOpaqueUnicodeBothPurposesUnderExtractWithoutPortalRights()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var content = "{\"private\":\" Tồn kho 😀�\uFEFF\t \"}";
        var seed = await SeedBrainAsync(f, claim, source, content); var counts = await f.CountsAsync();
        var reader = BrainReader(f); var context = await reader.ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]);
        Assert.Equal(content, context.Items[0].Content); Assert.Equal(GroupBrainContentKind.RequestRevision, context.Items[0].Kind);
        Assert.Equal("{\"term\":\"nhập–xuất 😀\"}", context.Items[1].Content);
        Assert.Equal(GroupNoteBusinessStatus.New, context.Items[0].BusinessStatus); Assert.False(context.Items[0].IsItConfirmed);
        Assert.Null(context.Items[1].BusinessStatus); Assert.Equal(1, f.Keys.Reads);
        Assert.Equal(counts, await f.CountsAsync()); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        Assert.DoesNotContain(content, context.ToString()); Assert.DoesNotContain(content, context.Items[0].ToString());
        Assert.Empty(typeof(GroupBrainPrivateContext).GetConstructors()); Assert.Empty(typeof(GroupBrainPrivateRevision).GetConstructors());
        await reader.RequireCurrentAsync(context); Assert.Equal(1, f.Keys.Reads);
    }

    [Fact]
    public async Task BrainEmptySelectionStillNeedsLiveExtractAndNeverKeys()
    {
        using var f = new Fixture(); await f.CommitAsync(); var claim = await f.ClaimAsync();
        var reader = BrainReader(f); var context = await reader.ReadAsync(claim, [], []); Assert.Empty(context.Items);
        Assert.Equal(0, f.Keys.Reads); f.Extract.IsEnabled = false; await f.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(claim, [], []));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.RequireCurrentAsync(context));
    }

    [Theory]
    [InlineData("foreign-id")]
    [InlineData("duplicate")]
    [InlineData("zero-id")]
    [InlineData("over-bound")]
    [InlineData("combined-bound")]
    public async Task BrainSelectionCannotBorrowAnotherIdDuplicateOrExceedBoundsBeforeKeys(string variant)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        var requests = variant switch
        {
            "foreign-id" => new[] { Guid.NewGuid() },
            "duplicate" => [seed.Request.Id, seed.Request.Id],
            "zero-id" => [Guid.Empty],
            "over-bound" => Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray(),
            _ => Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray()
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, requests,
            variant == "combined-bound" ? [seed.Glossary.Id] : [])); Assert.Equal(0, f.Keys.Reads);
    }

    [Theory]
    [InlineData("unconfirmed-resolved")]
    [InlineData("unconfirmed-assignee")]
    [InlineData("head-revision")]
    [InlineData("null-code")]
    [InlineData("origin")]
    [InlineData("null-claim")]
    [InlineData("source-generation")]
    [InlineData("missing-evidence")]
    [InlineData("evidence-ordinal")]
    [InlineData("evidence-foreign")]
    [InlineData("key-id")]
    [InlineData("cipher-size")]
    [InlineData("cipher-hash")]
    [InlineData("glossary-disabled")]
    [InlineData("glossary-no-extract")]
    [InlineData("glossary-publisher")]
    public async Task BrainMalformedMetadataOrOperatorPolicyRefusesBeforeKeys(string corruption)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        var evidence = await f.Auth.Db.GroupRequestEvidence.SingleAsync();
        switch (corruption)
        {
            case "unconfirmed-resolved": seed.Request.BusinessStatus = GroupNoteBusinessStatus.Resolved; break;
            case "unconfirmed-assignee": seed.Request.AssignedToUserId = Guid.NewGuid(); break;
            case "head-revision": seed.Request.CurrentRevision++; break;
            case "null-code": seed.Request.RequestCode = null!; break;
            case "origin": seed.Revision.Origin = (GroupRequestRevisionOrigin)99; break;
            case "null-claim": seed.Revision.ClaimEpoch = null; break;
            case "source-generation": seed.Revision.DeletionGeneration++; break;
            case "missing-evidence": f.Auth.Db.Remove(evidence); break;
            case "evidence-ordinal":
                f.Auth.Db.Remove(evidence); await f.Auth.Db.SaveChangesAsync();
                evidence.Ordinal = 2; f.Auth.Db.Add(evidence); break;
            case "evidence-foreign": evidence.MessageId = Guid.NewGuid(); break;
            case "key-id": seed.Revision.ContentKeyId = "PRIVATE/invalid"; break;
            case "cipher-size": seed.Revision.ProtectedContent = new byte[64030]; break;
            case "cipher-hash": seed.Revision.EnvelopeSha256 = "A".PadRight(64, 'A'); break;
            case "glossary-disabled": seed.Glossary.IsEnabled = false; break;
            case "glossary-no-extract": seed.Glossary.AllowExtraction = false; break;
            case "glossary-publisher": seed.GlossaryRevision.PublishedByUserId = Guid.NewGuid(); break;
        }
        await f.Auth.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]));
        Assert.Equal("Group brain context is unavailable.", error.Message); Assert.Null(error.InnerException); Assert.Equal(0, f.Keys.Reads);
    }

    [Theory]
    [InlineData(GroupSourceEventKind.Edit)]
    [InlineData(GroupSourceEventKind.Recall)]
    public async Task BrainChangedOrRecalledContributingSourceCannotReleaseOldRevision(GroupSourceEventKind kind)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        await f.CommitAsync(f.Payload(eventId: "later", kind: kind, text: kind == GroupSourceEventKind.Recall ? "" : "edited"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal(0, f.Keys.Reads);
    }

    [Theory]
    [InlineData("grant", true)]
    [InlineData("source-version", true)]
    [InlineData("head-business-version", false)]
    [InlineData("glossary-version", false)]
    [InlineData("glossary-enable", false)]
    [InlineData("evidence-kind", false)]
    [InlineData("source-recall", false)]
    public async Task BrainAllContributingChangesDuringOutsideSqlKeyAwaitWinBeforeDecryption(string change, bool authority)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        // Corrupt but hash-consistent ciphertext would fail AEAD if the post-key
        // dependency fence were skipped. Refusal categories prove fence order.
        seed.Revision.ProtectedContent = seed.Revision.ProtectedContent.ToArray(); seed.Revision.ProtectedContent[^1] ^= 1;
        seed.Revision.EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(seed.Revision.ProtectedContent));
        await f.Auth.Db.SaveChangesAsync();
        f.Keys.BeforeRead = async () =>
        {
            switch (change)
            {
                case "grant": f.Extract.IsEnabled = false; break;
                case "source-version": f.Auth.Binding.Version++; break;
                case "head-business-version": seed.Request.BusinessVersion++; break;
                case "glossary-version": seed.Glossary.Version++; break;
                case "glossary-enable": seed.Glossary.IsEnabled = false; break;
                case "evidence-kind": (await f.Auth.Db.GroupRequestEvidence.SingleAsync()).Kind = GroupRequestEvidenceKind.HostMetadataAttention; break;
                case "source-recall": await f.CommitAsync(f.Payload(eventId: "recall-during-key", kind: GroupSourceEventKind.Recall, text: "")); return;
            }
            await f.Auth.Db.SaveChangesAsync();
        };
        if (authority) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]));
            Assert.Equal("Group brain context is unavailable.", error.Message);
        }
        Assert.Equal(1, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task BrainExpiryDuringKeyAwaitPersistsOnlyWitnessAndCannotReviveOnClockRollback()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source); var counts = await f.CountsAsync();
        f.Keys.BeforeRead = () => { f.Auth.Clock.Current = claim.Receipt.ExpiresAtUtc; return Task.CompletedTask; };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal(claim.Receipt.ExpiresAtUtc, (await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        Assert.Equal(counts, await f.CountsAsync()); Assert.Equal(1, f.Keys.Reads);
        f.Auth.Clock.Current -= TimeSpan.FromTicks(1); f.Keys.BeforeRead = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal(1, f.Keys.Reads); Assert.Equal(1, await f.Auth.Db.GroupCustomerRequests.CountAsync());
    }

    [Fact]
    public async Task BrainRecheckRejectsSelectedChangeWithoutKeysButAllowsUnrelatedSourceIngress()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source); var reader = BrainReader(f);
        var context = await reader.ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]);
        await f.CommitAsync(f.Payload(messageId: "unrelated", eventId: "unrelated"));
        await reader.RequireCurrentAsync(context); Assert.Equal(1, f.Keys.Reads);
        seed.Glossary.Version++; await f.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.RequireCurrentAsync(context)); Assert.Equal(1, f.Keys.Reads);
    }

    [Fact]
    public async Task BrainSelectionIsFrozenAndForeignWorkerAndCancellationFailBeforeDisposedDatabase()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source); var ids = new[] { seed.Request.Id };
        f.Keys.BeforeRead = () => { ids[0] = Guid.NewGuid(); return Task.CompletedTask; };
        Assert.Equal(seed.Request.Id, Assert.Single((await BrainReader(f).ReadAsync(claim, ids, [])).Items).RecordId);
        f.Auth.Db.Dispose(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => BrainReader(f).ReadAsync(claim, [], [], canceled.Token));
        var foreign = new GroupBrainCurrentReader(f.Auth.Db, f.Worker with { CompanyId = Guid.NewGuid() }, f.Auth.Clock, f.Keys, new());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.ReadAsync(claim, [], []));
    }

    [Fact]
    public async Task BrainAggregateEnvelopeBoundFailsBeforeKeyLookup()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var ids = new List<Guid>();
        for (var index = 0; index < 4; index++) ids.Add((await SeedBrainAsync(f, claim, source, new string('a', 64000))).Request.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, ids, []));
        Assert.Equal(0, f.Keys.Reads);
        Assert.Equal(3, (await BrainReader(f).ReadAsync(claim, ids.Take(3).ToArray(), [])).Items.Count); Assert.Equal(1, f.Keys.Reads);
    }

    [Fact]
    public async Task BrainPurposeSubstitutionCannotDecryptEvenWhenCipherHashAndKeyAreValid()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        seed.Revision.ProtectedContent = seed.GlossaryRevision.ProtectedContent.ToArray(); seed.Revision.EnvelopeSha256 = seed.GlossaryRevision.EnvelopeSha256;
        await f.Auth.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal("Group brain content is unavailable.", error.Message); Assert.Null(error.InnerException); Assert.Equal(1, f.Keys.Reads);
    }

    [Fact]
    public async Task BrainSameIdInForeignSourceCannotBorrowCurrentExtractGrant()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source, scopeOverride: new(f.Auth.Scope.TenantId, f.Auth.Scope.CompanyId, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal(0, f.Keys.Reads);
    }

    private sealed class BrokenBrainKeys(bool returnsNull) : IGroupSourceKeyProvider
    {
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope scope, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Write resolution must not run.");
        public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope scope, string keyId, CancellationToken cancellationToken = default) =>
            returnsNull ? ValueTask.FromResult<GroupSourceKeyMaterial>(null!) : throw new IOException("PRIVATE_PROVIDER_CREDENTIAL_DETAIL");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrainBrokenKeyProviderCannotReleaseOrExposePrivateFailure(bool returnsNull)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        var reader = new GroupBrainCurrentReader(f.Auth.Db, f.Worker, f.Auth.Clock, new BrokenBrainKeys(returnsNull), new());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal("Group brain context is unavailable.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task BrainIgnoredResolverCancellationStopsAndDisposesLateKeyWithoutPrivateContext()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Keys.BeforeRead = async () => { entered.SetResult(); await release.Task; };
        using var canceled = new CancellationTokenSource();
        var read = BrainReader(f).ReadAsync(claim, [seed.Request.Id], [], canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(f.Keys.LastMaterial); release.SetResult(); await f.Keys.MaterialReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopped = false;
        for (var attempt = 0; attempt < 20 && !stopped; attempt++)
        {
            try { _ = f.Keys.LastMaterial!.Key.Length; } catch (ObjectDisposedException) { stopped = true; }
            if (!stopped) await Task.Delay(10);
        }
        Assert.True(stopped); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }
}
