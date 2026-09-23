using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerChatResponseProjectionTests
{
    private const long AuthorityVersion = 11;
    private readonly CustomerChatAuthority authority = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private CustomerChatResponseEvidence Evidence(params CustomerChatResponseAttachment[] attachments) =>
        new(authority, AuthorityVersion, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "done", attachments, "model-a", "provider-a", "worker-a");

    private CustomerChatResponseCheckpoint Prepare(CustomerChatResponseEvidence? evidence = null) =>
        CustomerChatResponseProjection.Prepare(authority, AuthorityVersion, evidence ?? Evidence());

    [Fact]
    public void Trusted_authority_cannot_be_overridden_by_worker_output()
    {
        var hostile = Evidence() with { Authority = authority with { CompanyId = Guid.NewGuid() } };
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(hostile));
    }

    [Fact]
    public void Stale_authority_fails_closed_for_projection_and_delivery()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(Evidence() with { AuthorityVersion = AuthorityVersion - 1 }));
        var persisted = CustomerChatResponseProjection.MarkPersisted(Prepare(), "checkpoint:response/1");
        Assert.Throws<UnauthorizedAccessException>(() => CustomerChatResponseProjection.AuthorizeDelivery(authority, AuthorityVersion + 1, persisted));
    }

    [Fact]
    public void Response_attachment_must_share_exact_conversation_authority()
    {
        var foreign = new CustomerChatResponseAttachment(authority with { ConversationId = Guid.NewGuid() }, Guid.NewGuid(), "object:response/1");
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(Evidence(foreign)));
    }

    [Fact]
    public void Attachment_and_persistence_references_reject_secret_material()
    {
        var secretAttachment = new CustomerChatResponseAttachment(authority, Guid.NewGuid(), "Secret=raw-secret");
        Assert.Throws<InvalidOperationException>(() => Prepare(Evidence(secretAttachment)));
        Assert.Throws<InvalidOperationException>(() => CustomerChatResponseProjection.MarkPersisted(Prepare(), "Password=raw-secret"));
    }

    [Fact]
    public void Delivery_requires_durable_persistence_first()
    {
        var prepared = Prepare();
        Assert.Throws<InvalidOperationException>(() => CustomerChatResponseProjection.AuthorizeDelivery(authority, AuthorityVersion, prepared));

        var persisted = CustomerChatResponseProjection.MarkPersisted(prepared, "checkpoint:response/2");
        var delivery = CustomerChatResponseProjection.AuthorizeDelivery(authority, AuthorityVersion, persisted);

        Assert.Equal(prepared.MessageId, delivery.MessageId);
        Assert.Equal(prepared.InReplyToMessageId, delivery.InReplyToMessageId);
        Assert.Equal(prepared.TaskId, delivery.TaskId);
        Assert.Equal("checkpoint:response/2", delivery.PersistenceReference);
    }

    [Fact]
    public void Exact_durable_replay_is_idempotent_but_conflicting_evidence_fails_closed()
    {
        var attachment = new CustomerChatResponseAttachment(authority, Guid.NewGuid(), "object:response/2");
        var persisted = CustomerChatResponseProjection.MarkPersisted(Prepare(Evidence(attachment)), "checkpoint:response/3");
        var reconstructed = persisted with { AttachmentIds = persisted.AttachmentIds.ToArray() };

        Assert.Same(persisted, CustomerChatResponseProjection.ResolveReplay(persisted, reconstructed));
        Assert.Throws<InvalidOperationException>(() => CustomerChatResponseProjection.ResolveReplay(persisted, reconstructed with { Content = "changed" }));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerChatResponseProjection.ResolveReplay(persisted, reconstructed with { TaskId = Guid.NewGuid() }));
    }

    [Fact]
    public void Model_provider_and_worker_are_diagnostics_not_delivery_authority()
    {
        var prepared = Prepare();
        Assert.Equal("model-a", prepared.Model);
        Assert.Equal("provider-a", prepared.Provider);
        Assert.Equal("worker-a", prepared.Worker);

        var delivery = CustomerChatResponseProjection.AuthorizeDelivery(
            authority,
            AuthorityVersion,
            CustomerChatResponseProjection.MarkPersisted(prepared, "checkpoint:response/4"));

        Assert.DoesNotContain(delivery.GetType().GetProperties(), p => p.Name.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(delivery.GetType().GetProperties(), p => p.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(delivery.GetType().GetProperties(), p => p.Name.Contains("Worker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_attachment_identity_and_malformed_response_identity_fail_closed()
    {
        var id = Guid.NewGuid();
        var first = new CustomerChatResponseAttachment(authority, id, "object:a");
        var second = new CustomerChatResponseAttachment(authority, id, "object:b");
        Assert.Throws<InvalidOperationException>(() => Prepare(Evidence(first, second)));
        Assert.Throws<InvalidOperationException>(() => Prepare(Evidence() with { InReplyToMessageId = Guid.Empty }));
        Assert.Throws<InvalidOperationException>(() => CustomerChatResponseProjection.Prepare(authority, 0, Evidence()));
    }
}
