using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerConversationContextTests
{
    private static readonly CustomerChatAuthority Authority = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Assemble_orders_deterministically_preserves_ancestry_and_excludes_future_turns()
    {
        var root = Message(Guid.Parse("00000000-0000-0000-0000-000000000001"), null, T0, CustomerConversationMessageRole.Customer);
        var reply = Message(Guid.Parse("00000000-0000-0000-0000-000000000002"), root.MessageId, T0, CustomerConversationMessageRole.Assistant);
        var selected = Message(Guid.Parse("00000000-0000-0000-0000-000000000003"), reply.MessageId, T0.AddMinutes(1), CustomerConversationMessageRole.Customer);
        var future = Message(Guid.Parse("00000000-0000-0000-0000-000000000004"), null, T0.AddMinutes(2), CustomerConversationMessageRole.Customer);

        var snapshot = CustomerConversationContext.Assemble(Authority, 7, selected.MessageId, 4, new[] { future, selected, reply, root });

        Assert.Equal(new[] { root.MessageId, reply.MessageId, selected.MessageId }, snapshot.Messages.Select(x => x.MessageId));
        Assert.DoesNotContain(snapshot.Messages, x => x.MessageId == future.MessageId);
    }

    [Fact]
    public void Assemble_rejects_cross_authority_and_stale_evidence()
    {
        var cross = Message(Guid.NewGuid(), null, T0, CustomerConversationMessageRole.Customer) with { Authority = Authority with { CompanyId = Guid.NewGuid() } };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerConversationContext.Assemble(Authority, 7, cross.MessageId, 5, new[] { cross }));
        var stale = Message(Guid.NewGuid(), null, T0, CustomerConversationMessageRole.Customer) with { AuthorityVersion = 6 };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerConversationContext.Assemble(Authority, 7, stale.MessageId, 5, new[] { stale }));
    }

    [Fact]
    public void Assemble_deduplicates_exact_evidence_but_rejects_conflict()
    {
        var message = Message(Guid.NewGuid(), null, T0, CustomerConversationMessageRole.Customer);
        var duplicate = message with { Attachments = message.Attachments.ToArray() };
        Assert.Single(CustomerConversationContext.Assemble(Authority, 7, message.MessageId, 5, new[] { message, duplicate }).Messages);
        Assert.Throws<InvalidOperationException>(() => CustomerConversationContext.Assemble(Authority, 7, message.MessageId, 5, new[] { message, message with { Content = "different" } }));
    }

    [Fact]
    public void Assemble_fails_when_window_cannot_preserve_reply_ancestry_or_parent_is_not_earlier()
    {
        var root = Message(Guid.NewGuid(), null, T0, CustomerConversationMessageRole.Customer);
        var reply = Message(Guid.NewGuid(), root.MessageId, T0.AddMinutes(1), CustomerConversationMessageRole.Assistant);
        var selected = Message(Guid.NewGuid(), reply.MessageId, T0.AddMinutes(2), CustomerConversationMessageRole.Customer);
        Assert.Throws<InvalidOperationException>(() => CustomerConversationContext.Assemble(Authority, 7, selected.MessageId, 2, new[] { root, reply, selected }));

        var lateParent = root with { OccurredAt = T0.AddMinutes(3) };
        Assert.Throws<InvalidOperationException>(() => CustomerConversationContext.Assemble(Authority, 7, reply.MessageId, 5, new[] { lateParent, reply }));
    }

    [Fact]
    public void Assemble_rejects_cross_authority_attachments_and_secret_material()
    {
        var message = Message(Guid.NewGuid(), null, T0, CustomerConversationMessageRole.Customer);
        var crossAttachment = message with { Attachments = new[] { new CustomerConversationContextAttachment(Authority with { UserId = Guid.NewGuid() }, Guid.NewGuid(), "object://safe") } };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerConversationContext.Assemble(Authority, 7, message.MessageId, 5, new[] { crossAttachment }));
        Assert.Throws<InvalidOperationException>(() => CustomerConversationContext.Assemble(Authority, 7, message.MessageId, 5, new[] { message with { PersistenceReference = "Password=do-not-leak" } }));
    }

    [Fact]
    public void Provider_diagnostics_do_not_change_authority_or_projection()
    {
        var message = Message(Guid.NewGuid(), null, T0, CustomerConversationMessageRole.Assistant) with { Model = "model-a", Provider = "provider-a", Worker = "worker-a" };
        var changed = message with { Model = "model-b", Provider = "provider-b", Worker = "worker-b" };
        var first = CustomerConversationContext.Assemble(Authority, 7, message.MessageId, 5, new[] { message });
        var second = CustomerConversationContext.Assemble(Authority, 7, message.MessageId, 5, new[] { changed });
        Assert.Equal(first.Authority, second.Authority);
        Assert.Equal(first.SelectedMessageId, second.SelectedMessageId);
        Assert.Equal(first.Messages.Select(x => x.MessageId), second.Messages.Select(x => x.MessageId));
    }

    private static CustomerConversationContextMessage Message(Guid id, Guid? parent, DateTimeOffset occurredAt, CustomerConversationMessageRole role) =>
        new(Authority, 7, id, parent, occurredAt, role, "content", new[] { new CustomerConversationContextAttachment(Authority, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "object://attachment") }, $"persist://{id:N}");
}
