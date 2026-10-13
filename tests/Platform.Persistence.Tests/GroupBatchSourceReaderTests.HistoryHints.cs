using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static string HistoryCode(int value) => "REQ-" + value.ToString("X32");

    [Fact]
    public async Task HistoryHintsUseCommittedSourceOrderCanonicalCodesAndExactReplyIndexWithoutEffects()
    {
        using var f = new Fixture();
        var first = f.Payload(text: "Xem (" + HistoryCode(2).ToLowerInvariant() + ") và " + HistoryCode(2));
        first = first with { Event = first.Event with { ReplyToMessageId = "PRIVATE_REPLY_90421😀 " } };
        var a = await f.CommitAsync(first);
        var b = await f.CommitAsync(f.Payload(messageId: "later", eventId: "later-event", text: HistoryCode(1)));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [b.MessageId, a.MessageId]));
        var before = await f.CountsAsync(); var reads = f.Keys.Reads;
        var hints = GroupAutomaticHistoryHints.Create(preparation);
        Assert.Equal(f.Auth.Scope, hints.Scope);
        Assert.Equal(new[] { HistoryCode(2), HistoryCode(1) }, hints.RequestCodes);
        Assert.Equal(GroupIngressIdentity.MessageIndex(f.Auth.Scope, first.Event.ReplyToMessageId!), Assert.Single(hints.ReplyMessageIndexes));
        Assert.False(hints.HasAdditionalRequestCodes);
        Assert.DoesNotContain("PRIVATE_REPLY_90421", hints.ToString()); Assert.DoesNotContain(HistoryCode(2), hints.ToString());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(reads, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(hints.RequestCodes).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(hints.ReplyMessageIndexes).Clear());
    }

    [Fact]
    public async Task HistoryHintsExcludeQuarantinedCodesAndRepliesAndRetainOriginalSource()
    {
        using var f = new Fixture();
        var payload = f.Payload(text: "Password=PRIVATE_SENTINEL_72691 " + HistoryCode(1));
        payload = payload with { Event = payload.Event with { ReplyToMessageId = "QUARANTINED_REPLY" } };
        var source = await f.CommitAsync(payload);
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        Assert.Equal(GroupSourcePreparationDisposition.Quarantined, Assert.Single(preparation.Receipts).Disposition);
        var hints = GroupAutomaticHistoryHints.Create(preparation);
        Assert.Empty(hints.RequestCodes); Assert.Empty(hints.ReplyMessageIndexes);
        Assert.Equal(payload.Text, Assert.Single(preparation.Context.Items).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryHintsExcludeRecalledAndChangedAfterCutoffSources(bool changedAfterCutoff)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: HistoryCode(1)));
        var claim = changedAfterCutoff ? await f.ClaimAsync() : null;
        await f.CommitAsync(f.Payload(eventId: "changed", kind: changedAfterCutoff ? GroupSourceEventKind.Edit : GroupSourceEventKind.Recall,
            text: changedAfterCutoff ? HistoryCode(2) : ""));
        claim ??= await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        Assert.Empty(preparation.Candidates);
        Assert.Empty(GroupAutomaticHistoryHints.Create(preparation).RequestCodes);
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("underscore")]
    [InlineData("unicode-prefix")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("nonhex")]
    [InlineData("empty-id")]
    public async Task HistoryHintsRefuseMalformedAndEmbeddedRequestCodes(string fault)
    {
        using var f = new Fixture(); var code = HistoryCode(1);
        var text = fault switch
        {
            "prefix" => "X" + code,
            "suffix" => code + "X",
            "underscore" => "_" + code,
            "unicode-prefix" => "界" + code,
            "short" => code[..^1],
            "long" => code + "1",
            "nonhex" => code[..^1] + "G",
            _ => HistoryCode(0)
        };
        var source = await f.CommitAsync(f.Payload(text: text));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        Assert.Empty(GroupAutomaticHistoryHints.Create(preparation).RequestCodes);
    }

    [Theory]
    [InlineData(0x10400, true, false)]
    [InlineData(0x10400, false, false)]
    [InlineData(0x1D7CE, true, false)]
    [InlineData(0x1D7CE, false, false)]
    [InlineData(0x1F600, true, true)]
    [InlineData(0x1F600, false, true)]
    public async Task HistoryHintsUseCompleteUnicodeScalarBoundariesForBothSides(int scalar, bool prefix, bool accepted)
    {
        using var f = new Fixture(); var adjacent = char.ConvertFromUtf32(scalar); var code = HistoryCode(1);
        var source = await f.CommitAsync(f.Payload(text: prefix ? adjacent + code : code + adjacent));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var hints = GroupAutomaticHistoryHints.Create(preparation);
        if (accepted) Assert.Equal(code, Assert.Single(hints.RequestCodes));
        else Assert.Empty(hints.RequestCodes);
        Assert.False(hints.HasAdditionalRequestCodes);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public async Task HistoryHintsBoundDistinctLookupCodesAndDiscloseOverflow(int count, bool overflow)
    {
        using var f = new Fixture();
        var source = await f.CommitAsync(f.Payload(text: string.Join(" ", Enumerable.Range(1, count).Select(HistoryCode))));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var hints = GroupAutomaticHistoryHints.Create(preparation);
        Assert.Equal(Enumerable.Range(1, 100).Select(HistoryCode), hints.RequestCodes);
        Assert.Equal(overflow, hints.HasAdditionalRequestCodes);
    }

    [Fact]
    public async Task HistoryHintsKeepOpaqueReplyWhitespaceAndDeduplicateOnlyExactIndexes()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var payload = f.Payload(messageId: "m" + index, eventId: "e" + index, text: "Tra cứu lại");
            payload = payload with { Event = payload.Event with { ReplyToMessageId = index == 2 ? "reply " : "reply" } };
            ids.Add((await f.CommitAsync(payload)).MessageId);
        }
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), ids));
        var hints = GroupAutomaticHistoryHints.Create(preparation);
        Assert.Equal(new[] { GroupIngressIdentity.MessageIndex(f.Auth.Scope, "reply"), GroupIngressIdentity.MessageIndex(f.Auth.Scope, "reply ") }, hints.ReplyMessageIndexes);
    }

    [Fact]
    public async Task HistoryHintsCancelBeforeReturningAnyLookupOrEffects()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: HistoryCode(1)));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupAutomaticHistoryHints.Create(preparation, cancelled.Token));
        Assert.Throws<ArgumentNullException>(() => GroupAutomaticHistoryHints.Create(null!));
        Assert.Empty(typeof(GroupAutomaticHistoryHints).GetConstructors());
    }
}
