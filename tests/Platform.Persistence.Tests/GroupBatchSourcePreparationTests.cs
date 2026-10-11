using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task FormatOnlySourceIsHostDispositionWithOriginalRetained()
    {
        using var f = new Fixture(); var text = "\uFEFF \u200B\u2060\r\n";
        var message = await f.CommitAsync(f.Payload(text: text));
        var context = await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]);
        var preparation = GroupBatchSourcePreparation.Create(context);
        Assert.Empty(preparation.Candidates); Assert.Equal(text, Assert.Single(context.Items).Text);
        Assert.Equal(GroupSourcePreparationDisposition.EmptyText, Assert.Single(preparation.Receipts).Disposition);
    }

    [Fact]
    public async Task PreparationExcludesQuarantinedSourceWithoutPrivateAttentionMetadataAndRetainsOriginal()
    {
        using var f = new Fixture();
        var sensitive = await f.CommitAsync(f.Payload(text: "Password=PRIVATE_SENTINEL_72691"));
        var useful = await f.CommitAsync(f.Payload(messageId: "other-private-external", eventId: "other-event", text: "Tra cứu tồn kho SP-00012"));
        var claim = await f.ClaimAsync(); var before = await f.CountsAsync();
        var context = await f.Reader.ReadAsync(claim, [sensitive.MessageId, useful.MessageId]);
        var preparation = GroupBatchSourcePreparation.Create(context);
        var candidate = Assert.Single(preparation.Candidates);
        Assert.Equal(useful.MessageId, candidate.MessageId); Assert.Equal("Tra cứu tồn kho SP-00012", candidate.Text);
        Assert.Equal("Password=PRIVATE_SENTINEL_72691", context.Items[0].Text);
        Assert.Equal(2, preparation.Receipts.Count);
        var withheld = preparation.Receipts.Single(x => x.MessageId == sensitive.MessageId);
        Assert.Equal(GroupSourcePreparationDisposition.Quarantined, withheld.Disposition);
        Assert.Equal(GroupSecretQuarantineReason.CredentialAssignment, withheld.QuarantineReason);
        Assert.Equal(GroupSecretQuarantine.PolicyVersion, preparation.QuarantinePolicyVersion);
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", JsonSerializer.Serialize(preparation.Receipts));
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", JsonSerializer.Serialize(preparation.Candidates));
        Assert.DoesNotContain("other-private-external", JsonSerializer.Serialize(preparation.Candidates));
        Assert.Equal(before, await f.CountsAsync());
        Assert.DoesNotContain(candidate.Text, candidate.ToString()); Assert.DoesNotContain(candidate.Text, preparation.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownMediaKeepsHostAttentionWithOrWithoutModelEligibleCaption(bool caption)
    {
        using var f = new Fixture();
        var message = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Media, text: caption ? "Lỗi nhập kho xem ảnh" : ""));
        var context = await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]);
        var preparation = GroupBatchSourcePreparation.Create(context); var receipt = Assert.Single(preparation.Receipts);
        Assert.True(receipt.HasUnsupportedMedia); Assert.Null(receipt.QuarantineReason);
        Assert.Equal(caption ? GroupSourcePreparationDisposition.ModelText : GroupSourcePreparationDisposition.EmptyText, receipt.Disposition);
        Assert.Equal(caption ? 1 : 0, preparation.Candidates.Count);
    }

    [Fact]
    public async Task RecalledOriginalWithCredentialNeverBecomesCandidateOrNewSecretDerivedMetadata()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Password=PRIVATE_SENTINEL_72691"));
        await f.CommitAsync(f.Payload(eventId: "recall", kind: GroupSourceEventKind.Recall, text: ""));
        var context = await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]);
        var preparation = GroupBatchSourcePreparation.Create(context);
        Assert.Empty(preparation.Candidates); Assert.Equal(0, f.Keys.Reads);
        var receipt = Assert.Single(preparation.Receipts);
        Assert.Equal(GroupSourcePreparationDisposition.Recalled, receipt.Disposition); Assert.Null(receipt.QuarantineReason);
    }

    [Fact]
    public void PreparationAndProviderCandidateCannotBeForgedOrMutated()
    {
        Assert.Empty(typeof(GroupBatchSourcePreparation).GetConstructors()); Assert.Empty(typeof(GroupPreparedModelSource).GetConstructors());
        Assert.All(typeof(GroupPreparedModelSource).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.All(typeof(GroupBatchSourcePreparation).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.Throws<ArgumentNullException>(() => GroupBatchSourcePreparation.Create(null!));
    }
}
