using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static GroupAutomaticHistorySelector HistorySelector(Fixture f) => new(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader);
    private static void OlderHistory(BrainSeed seed, string? code = null)
    {
        seed.Request.OriginBatchId = Guid.NewGuid();
        if (code is not null) seed.Request.RequestCode = code;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task HistorySelectionActualQueriesTranslateToBoundedScopedMetadataSqlOffline(int query)
    {
        using var f = new Fixture();
        var payload = f.Payload(text: HistoryCode(2)); payload = payload with { Event = payload.Event with { ReplyToMessageId = "PRIVATE_REPLY" } };
        var source = await f.CommitAsync(payload);
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=offline_history_query_translation;Integrated Security=true;Connect Timeout=1").Options);
        var selector = new GroupAutomaticHistorySelector(db, f.Worker, f.Auth.Clock, f.Reader);
        var sql = query == 3 ? selector.QueryGlossary(prep).ToQueryString()
            : selector.QueryRequests(prep, GroupAutomaticHistoryHints.Create(prep), query).ToQueryString();
        Assert.Contains("TOP(@", sql); Assert.Contains(query == 3 ? " = 5;" : " = 21;", sql);
        foreach (var id in new[] { f.Auth.Scope.TenantId, f.Auth.Scope.CompanyId, f.Auth.Scope.SourceBindingId }) Assert.Contains(id.ToString(), sql);
        foreach (var column in new[] { "[TenantId]", "[CompanyId]", "[BindingId]", "[SourceVersion]", "[DeletionGeneration]" }) Assert.Contains(column, sql);
        Assert.DoesNotContain("ProtectedContent", sql); Assert.DoesNotContain("ExternalMessageId", sql);
        Assert.DoesNotContain("ReplyToMessageId", sql); Assert.DoesNotContain("PRIVATE_REPLY", sql);
        if (query == 1) { Assert.Contains("GroupRequestEvidence", sql); Assert.Contains("GroupMessages", sql); Assert.Contains("[RequestRevision]", sql); }
        if (query != 3) Assert.Contains("[OriginBatchId] <>", sql);
        Assert.Null(db.Database.CurrentTransaction); Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task HistorySelectionFindsExplicitCodeAheadOfRecentSameGroupAndPublishedGlossaryWithoutKeys()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: "Bổ sung " + HistoryCode(2)));
        var claim = await f.ClaimAsync();
        var coded = await SeedBrainAsync(f, claim, source); OlderHistory(coded, HistoryCode(2));
        var recent = await SeedBrainAsync(f, claim, source); OlderHistory(recent);
        recent.Request.UpdatedAtUtc += TimeSpan.FromSeconds(1);
        await f.Auth.Db.SaveChangesAsync();
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var selection = await HistorySelector(f).SelectAsync(prep);
        Assert.Equal(new[] { coded.Request.Id, recent.Request.Id }, selection.RequestIds);
        Assert.Equal(2, selection.GlossaryIds.Count); Assert.False(selection.HasAdditionalHistory);
        Assert.Equal(f.Auth.Scope, selection.Scope); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(before, await f.CountsAsync()); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        Assert.Null(f.Auth.Db.Database.CurrentTransaction);
        Assert.DoesNotContain(HistoryCode(2), selection.ToString());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<Guid>>(selection.RequestIds).Clear());
    }

    [Fact]
    public async Task HistorySelectionUsesExactScopedReplyEvidenceAndDoesNotTreatHostAttentionAsCustomerQuote()
    {
        using var f = new Fixture();
        var payload = f.Payload(text: "Bổ sung lỗi nhập–xuất");
        payload = payload with { Event = payload.Event with { ReplyToMessageId = payload.Event.MessageId } };
        var source = await f.CommitAsync(payload); var claim = await f.ClaimAsync();
        var linked = await SeedBrainAsync(f, claim, source); OlderHistory(linked);
        var unlinked = await SeedBrainAsync(f, claim, source); OlderHistory(unlinked);
        unlinked.Request.UpdatedAtUtc += TimeSpan.FromSeconds(1);
        var evidence = f.Auth.Db.GroupRequestEvidence.Single(x => x.RequestId == unlinked.Request.Id);
        evidence.Kind = GroupRequestEvidenceKind.HostMetadataAttention;
        await f.Auth.Db.SaveChangesAsync();
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var selection = await HistorySelector(f).SelectAsync(prep);
        Assert.Equal(new[] { linked.Request.Id, unlinked.Request.Id }, selection.RequestIds);
        // Selection does not reinterpret the malformed/host record or decrypt it;
        // the normal BrainReader separately validates every selected revision.
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("source-version")]
    [InlineData("deletion-generation")]
    [InlineData("current-batch")]
    public async Task HistorySelectionNeverBorrowsForeignObsoleteOrCurrentBatchMetadata(string variant)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: HistoryCode(2))); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source); OlderHistory(seed, HistoryCode(2));
        switch (variant)
        {
            case "tenant": seed.Request.TenantId = Guid.NewGuid(); break;
            case "company": seed.Request.CompanyId = Guid.NewGuid(); break;
            case "binding": seed.Request.BindingId = Guid.NewGuid(); break;
            case "source-version": seed.Request.SourceVersion++; break;
            case "deletion-generation": seed.Request.DeletionGeneration++; break;
            default: seed.Request.OriginBatchId = claim.Receipt.BatchId; break;
        }
        // Scope keys are primary keys; replace the tracked head rather than
        // mutating a saved EF key. This is a cold corruption/control fixture.
        f.Auth.Db.ChangeTracker.Clear();
        var original = await f.Auth.Db.GroupCustomerRequests.FindAsync(f.Auth.Scope.TenantId, f.Auth.Scope.CompanyId,
            f.Auth.Scope.SourceBindingId, seed.Request.Id);
        Assert.NotNull(original); f.Auth.Db.Remove(original); await f.Auth.Db.SaveChangesAsync();
        f.Auth.Db.Add(seed.Request); await f.Auth.Db.SaveChangesAsync();
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var selection = await HistorySelector(f).SelectAsync(prep);
        Assert.Empty(selection.RequestIds);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("edited")]
    [InlineData("expired")]
    [InlineData("dirty")]
    public async Task HistorySelectionRechecksLiveAuthoritySourceAndCleanTrackerBeforeRelease(string variant)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        switch (variant)
        {
            case "revoked": f.Extract.IsEnabled = false; await f.Auth.Db.SaveChangesAsync(); break;
            case "edited": await f.CommitAsync(f.Payload(eventId: "new-edit", kind: GroupSourceEventKind.Edit, text: "changed")); break;
            case "expired": f.Auth.Clock.Current = claim.Receipt.ExpiresAtUtc; break;
            default: f.Extract.IsEnabled = false; break;
        }
        if (variant == "dirty") await Assert.ThrowsAsync<InvalidOperationException>(() => HistorySelector(f).SelectAsync(prep));
        else if (variant == "edited") await Assert.ThrowsAsync<InvalidOperationException>(() => HistorySelector(f).SelectAsync(prep));
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => HistorySelector(f).SelectAsync(prep));
        Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task HistorySelectionCancelsAndRefusesAnotherReaderContext()
    {
        using var f = new Fixture(); using var other = new Fixture();
        var source = await f.CommitAsync(); var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => HistorySelector(f).SelectAsync(prep, cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupAutomaticHistorySelector(f.Auth.Db, f.Worker, f.Auth.Clock, other.Reader).SelectAsync(prep));
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void HistoryRankingBoundsCombinedRequestAndGlossarySelectionAndDisclosesLimits(int count, bool limited)
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var rows = Enumerable.Range(0, count).Select(_ => new GroupAutomaticHistorySelector.Candidate(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch)).ToArray();
        var selection = GroupAutomaticHistorySelector.Rank(scope, rows, rows, [], [], false);
        Assert.Equal(rows.Take(20).Select(x => x.Id), selection.RequestIds); Assert.Empty(selection.GlossaryIds);
        Assert.Equal(limited, selection.HasAdditionalHistory);
        var glossary = new[] { new GroupAutomaticHistorySelector.Candidate(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch) };
        var full = GroupAutomaticHistorySelector.Rank(scope, rows, [], [], glossary, false);
        Assert.True(full.HasAdditionalHistory); Assert.Empty(full.GlossaryIds);
        var empty = GroupAutomaticHistorySelector.Rank(scope, [], [], [], glossary, true);
        Assert.True(empty.HasAdditionalHistory); Assert.Single(empty.GlossaryIds);
    }

    [Theory]
    [InlineData("zero-id")]
    [InlineData("revision")]
    [InlineData("time-zone")]
    [InlineData("duplicate")]
    [InlineData("over-bound")]
    public void HistoryRankingRejectsMalformedMetadataInsteadOfDroppingIt(string variant)
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var row = new GroupAutomaticHistorySelector.Candidate(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch);
        var values = variant switch
        {
            "zero-id" => [row with { Id = Guid.Empty }],
            "revision" => [row with { Revision = 0 }],
            "time-zone" => [row with { AtUtc = row.AtUtc.ToOffset(TimeSpan.FromHours(7)) }],
            "duplicate" => new[] { row, row },
            _ => Enumerable.Range(0, 22).Select(_ => row with { Id = Guid.NewGuid() }).ToArray()
        };
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticHistorySelector.Rank(scope, values, [], [], [], false));
    }
}
