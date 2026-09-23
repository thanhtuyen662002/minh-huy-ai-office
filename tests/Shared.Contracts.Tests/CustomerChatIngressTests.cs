using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerChatIngressTests
{
    private const long AuthorityVersion = 7;
    private readonly CustomerChatAuthority authority = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private CustomerChatIngressRequest Request(params CustomerChatAttachment[] attachments) => new(authority, Guid.NewGuid(), AuthorityVersion, "hello", attachments, "erp", "strong-model", "provider-a");

    private CustomerChatCheckpoint Prepare(CustomerChatIngressRequest? request = null) => CustomerChatIngress.Prepare(authority, AuthorityVersion, request ?? Request());

    [Fact]
    public void Trusted_authority_cannot_be_overridden_by_caller()
    {
        var hostile = Request() with { Authority = authority with { CompanyId = Guid.NewGuid() } };
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(hostile));
    }

    [Fact]
    public void Stale_authority_version_fails_closed()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(Request() with { AuthorityVersion = AuthorityVersion - 1 }));
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.Prepare(authority, 0, Request()));
    }

    [Fact]
    public void Attachment_must_share_exact_conversation_authority()
    {
        var foreign = new CustomerChatAttachment(authority with { ConversationId = Guid.NewGuid() }, Guid.NewGuid(), "object:attachment/1");
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(Request(foreign)));
    }

    [Fact]
    public void Attachment_and_persistence_references_reject_secret_material()
    {
        var secretAttachment = new CustomerChatAttachment(authority, Guid.NewGuid(), "Password=hunter2");
        Assert.Throws<InvalidOperationException>(() => Prepare(Request(secretAttachment)));
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.MarkPersisted(Prepare(), "AccountKey=raw-secret"));
    }

    [Fact]
    public void Dispatch_requires_persistence_first()
    {
        var prepared = Prepare();
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.AuthorizeDispatch(prepared));
        var persisted = CustomerChatIngress.MarkPersisted(prepared, "checkpoint:chat/1");
        var dispatch = CustomerChatIngress.AuthorizeDispatch(persisted);
        Assert.Equal(prepared.MessageId, dispatch.MessageId);
        Assert.Equal("checkpoint:chat/1", dispatch.PersistenceReference);
    }

    [Fact]
    public void Structural_durable_replay_is_idempotent_but_conflicting_evidence_fails_closed()
    {
        var attachment = new CustomerChatAttachment(authority, Guid.NewGuid(), "object:a");
        var persisted = CustomerChatIngress.MarkPersisted(Prepare(Request(attachment)), "checkpoint:chat/2");
        var reconstructed = persisted with { AttachmentIds = persisted.AttachmentIds.ToArray() };
        Assert.Same(persisted, CustomerChatIngress.ResolveReplay(persisted, reconstructed));
        Assert.Throws<InvalidOperationException>(() => CustomerChatIngress.ResolveReplay(persisted, reconstructed with { Content = "changed" }));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerChatIngress.ResolveReplay(persisted, reconstructed with { Authority = authority with { TenantId = Guid.NewGuid() } }));
    }

    [Fact]
    public void Routing_values_remain_hints_and_never_enter_dispatch_authority()
    {
        var prepared = Prepare();
        Assert.Equal("erp", prepared.RequestedAgentRole);
        Assert.Equal("strong-model", prepared.RequestedModel);
        Assert.Equal("provider-a", prepared.RequestedProvider);
        var dispatch = CustomerChatIngress.AuthorizeDispatch(CustomerChatIngress.MarkPersisted(prepared, "checkpoint:chat/3"));
        Assert.DoesNotContain(dispatch.GetType().GetProperties(), p => p.Name.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dispatch.GetType().GetProperties(), p => p.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dispatch.GetType().GetProperties(), p => p.Name.Contains("Agent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_attachment_identity_fails_closed()
    {
        var id = Guid.NewGuid();
        var first = new CustomerChatAttachment(authority, id, "object:a");
        var second = new CustomerChatAttachment(authority, id, "object:b");
        Assert.Throws<InvalidOperationException>(() => Prepare(Request(first, second)));
    }
}
