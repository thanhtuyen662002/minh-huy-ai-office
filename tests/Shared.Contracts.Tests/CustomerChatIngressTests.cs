using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerChatIngressTests
{
    private readonly CustomerChatAuthority authority = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private CustomerChatIngressRequest Request(params CustomerChatAttachment[] attachments) =>
        new(authority, Guid.NewGuid(), 7, "hello", attachments, "erp", "strong-model", "provider-a");

    [Fact]
    public void Trusted_authority_cannot_be_overridden_by_caller()
    {
        var hostile = Request() with { Authority = authority with { CompanyId = Guid.NewGuid() } };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerChatIngress.Prepare(authority, hostile));
    }

    [Fact]
    public void Attachment_must_share_exact_conversation_authority()
    {
        var foreign = new CustomerChatAttachment(authority with { ConversationId = Guid.NewGuid() }, Guid.NewGuid(), "object:attachment/1");
        Assert.Throws<UnauthorizedAccessException>(() => CustomerChatIngress.Prepare(authority, Request(foreign)));
    }

    [Fact]
    public void Attachment_and_persistence_references_reject_secret_material()
    {
        var secretAttachment = new CustomerChatAttachment(authority, Guid.NewGuid(), "Password=hunter2");
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.Prepare(authority, Request(secretAttachment)));

        var prepared = CustomerChatIngress.Prepare(authority, Request());
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.MarkPersisted(prepared, "AccountKey=raw-secret"));
    }

    [Fact]
    public void Dispatch_requires_persistence_first()
    {
        var prepared = CustomerChatIngress.Prepare(authority, Request());
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.AuthorizeDispatch(prepared));

        var persisted = CustomerChatIngress.MarkPersisted(prepared, "checkpoint:chat/1");
        var dispatch = CustomerChatIngress.AuthorizeDispatch(persisted);
        Assert.Equal(prepared.MessageId, dispatch.MessageId);
        Assert.Equal("checkpoint:chat/1", dispatch.PersistenceReference);
    }

    [Fact]
    public void Exact_durable_replay_is_idempotent_but_conflicting_evidence_fails_closed()
    {
        var persisted = CustomerChatIngress.MarkPersisted(CustomerChatIngress.Prepare(authority, Request()), "checkpoint:chat/2");
        Assert.Same(persisted, CustomerChatIngress.ResolveReplay(persisted, persisted));
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.ResolveReplay(persisted, persisted with { Content = "changed" }));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerChatIngress.ResolveReplay(persisted, persisted with { Authority = authority with { TenantId = Guid.NewGuid() } }));
    }

    [Fact]
    public void Routing_values_remain_hints_and_never_enter_dispatch_authority()
    {
        var prepared = CustomerChatIngress.Prepare(authority, Request());
        Assert.Equal("erp", prepared.RequestedAgentRole);
        Assert.Equal("strong-model", prepared.RequestedModel);
        Assert.Equal("provider-a", prepared.RequestedProvider);

        var dispatch = CustomerChatIngress.AuthorizeDispatch(CustomerChatIngress.MarkPersisted(prepared, "checkpoint:chat/3"));
        Assert.DoesNotContain(dispatch.GetType().GetProperties(), property => property.Name.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dispatch.GetType().GetProperties(), property => property.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dispatch.GetType().GetProperties(), property => property.Name.Contains("Agent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Malformed_authority_version_and_duplicate_attachment_identity_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.Prepare(authority, Request() with { AuthorityVersion = 0 }));
        var id = Guid.NewGuid();
        var first = new CustomerChatAttachment(authority, id, "object:a");
        var second = new CustomerChatAttachment(authority, id, "object:b");
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.Prepare(authority, Request(first, second)));
    }
}