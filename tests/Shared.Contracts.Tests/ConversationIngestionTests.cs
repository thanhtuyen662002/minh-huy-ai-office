using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class ConversationIngestionTests
{
    private static readonly ConversationIngestionScope Scope = new("tenant-a", "company-a", "task-a", "conversation-a");
    private static readonly DateTimeOffset Utc = new(2026, 9, 21, 21, 45, 0, TimeSpan.Zero);

    [Fact]
    public void Validate_OrdersMixedTextAndAttachmentMessagesDeterministically()
    {
        var attachment = new AttachmentManifest("att-1", "msg-2", "application/pdf", 42, "sha256:abc", "object://attachment/att-1");
        var manifest = ConversationIngestion.Validate(Scope,
        [
            new("msg-2", Scope, 2, Utc, null, [attachment]),
            new("msg-1", Scope, 1, Utc, "hello", [])
        ]);

        Assert.Equal(["msg-1", "msg-2"], manifest.Messages.Select(x => x.MessageId));
        Assert.Equal("att-1", manifest.Messages[1].Attachments[0].AttachmentId);
    }

    [Fact]
    public void Validate_RejectsDuplicateMessageAndAttachmentIdentity()
    {
        var duplicateMessages = new[]
        {
            new ConversationMessageManifest("same", Scope, 1, Utc, "one", []),
            new ConversationMessageManifest("same", Scope, 2, Utc, "two", [])
        };
        Assert.Throws<InvalidOperationException>(() => ConversationIngestion.Validate(Scope, duplicateMessages));

        var attachment = new AttachmentManifest("same-att", "msg-1", "image/png", 1, "sha256:a", "object://a");
        var second = attachment with { MessageId = "msg-2" };
        Assert.Throws<InvalidOperationException>(() => ConversationIngestion.Validate(Scope,
        [
            new("msg-1", Scope, 1, Utc, null, [attachment]),
            new("msg-2", Scope, 2, Utc, null, [second])
        ]));
    }

    [Fact]
    public void Validate_RejectsCrossScopeAndAttachmentOwnership()
    {
        var foreign = Scope with { CompanyId = "company-b" };
        Assert.Throws<InvalidOperationException>(() => ConversationIngestion.Validate(Scope,
            [new("msg-1", foreign, 1, Utc, "text", [])]));

        var wrongOwner = new AttachmentManifest("att-1", "other-message", "image/png", 1, "sha256:a", "object://a");
        Assert.Throws<InvalidOperationException>(() => ConversationIngestion.Validate(Scope,
            [new("msg-1", Scope, 1, Utc, null, [wrongOwner])]));
    }

    [Fact]
    public void Validate_RejectsMalformedAuthorityMetadataAndTimestamp()
    {
        var padded = Scope with { TenantId = " tenant-a" };
        Assert.Throws<ArgumentException>(() => ConversationIngestion.Validate(padded, []));

        var nonUtc = new DateTimeOffset(2026, 9, 22, 4, 45, 0, TimeSpan.FromHours(7));
        Assert.Throws<ArgumentException>(() => ConversationIngestion.Validate(Scope,
            [new("msg-1", Scope, 1, nonUtc, "text", [])]));

        var malformed = new AttachmentManifest("att-1", "msg-1", " image/png", -1, "sha256:a", "object://a");
        Assert.Throws<ArgumentException>(() => ConversationIngestion.Validate(Scope,
            [new("msg-1", Scope, 1, Utc, null, [malformed])]));
    }

    [Fact]
    public void Validate_RejectsEmptyMessageAndDuplicateSequence()
    {
        Assert.Throws<ArgumentException>(() => ConversationIngestion.Validate(Scope,
            [new("msg-1", Scope, 1, Utc, null, [])]));

        Assert.Throws<InvalidOperationException>(() => ConversationIngestion.Validate(Scope,
        [
            new("msg-1", Scope, 1, Utc, "one", []),
            new("msg-2", Scope, 1, Utc, "two", [])
        ]));
    }
}
