namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerChatAuthority(Guid TenantId, Guid CompanyId, Guid UserId, Guid ConversationId)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty || UserId == Guid.Empty || ConversationId == Guid.Empty)
            throw new InvalidOperationException("Customer chat authority requires tenant, company, user and conversation.");
    }
}

public sealed record CustomerChatAttachment(CustomerChatAuthority Authority, Guid AttachmentId, string ObjectReference);

public sealed record CustomerChatIngressRequest(
    CustomerChatAuthority Authority,
    Guid MessageId,
    long AuthorityVersion,
    string Content,
    IReadOnlyList<CustomerChatAttachment> Attachments,
    string? RequestedAgentRole = null,
    string? RequestedModel = null,
    string? RequestedProvider = null);

public sealed record CustomerChatCheckpoint(
    CustomerChatAuthority Authority,
    Guid MessageId,
    long AuthorityVersion,
    string Content,
    IReadOnlyList<Guid> AttachmentIds,
    string? RequestedAgentRole,
    string? RequestedModel,
    string? RequestedProvider,
    string PersistenceReference);

public sealed record CustomerChatDispatchAuthorization(
    CustomerChatAuthority Authority,
    Guid MessageId,
    long AuthorityVersion,
    string PersistenceReference);

public static class CustomerChatIngress
{
    public static CustomerChatCheckpoint Prepare(CustomerChatAuthority trustedAuthority, CustomerChatIngressRequest request)
    {
        trustedAuthority.Validate();
        if (request is null) throw new ArgumentNullException(nameof(request));
        RequireSameAuthority(trustedAuthority, request.Authority);
        if (request.MessageId == Guid.Empty || request.AuthorityVersion <= 0 || string.IsNullOrWhiteSpace(request.Content))
            throw new InvalidOperationException("Chat ingress requires durable message identity, authority version and content.");
        if (request.Attachments is null) throw new InvalidOperationException("Attachment collection is required.");

        var attachmentIds = new HashSet<Guid>();
        foreach (var attachment in request.Attachments)
        {
            if (attachment is null) throw new InvalidOperationException("Attachment is required.");
            RequireSameAuthority(trustedAuthority, attachment.Authority);
            if (attachment.AttachmentId == Guid.Empty || string.IsNullOrWhiteSpace(attachment.ObjectReference))
                throw new InvalidOperationException("Attachment requires durable identity and opaque object reference.");
            RejectSecretMaterial(attachment.ObjectReference);
            if (!attachmentIds.Add(attachment.AttachmentId))
                throw new InvalidOperationException("Duplicate attachment identity is ambiguous.");
        }

        return new CustomerChatCheckpoint(
            trustedAuthority,
            request.MessageId,
            request.AuthorityVersion,
            request.Content,
            attachmentIds.Order().ToArray(),
            NormalizeHint(request.RequestedAgentRole),
            NormalizeHint(request.RequestedModel),
            NormalizeHint(request.RequestedProvider),
            string.Empty);
    }

    public static CustomerChatCheckpoint MarkPersisted(CustomerChatCheckpoint prepared, string persistenceReference)
    {
        ValidateCheckpoint(prepared, requirePersistence: false);
        if (string.IsNullOrWhiteSpace(persistenceReference))
            throw new InvalidOperationException("Durable persistence evidence is required before dispatch.");
        RejectSecretMaterial(persistenceReference);
        return prepared with { PersistenceReference = persistenceReference };
    }

    public static CustomerChatDispatchAuthorization AuthorizeDispatch(CustomerChatCheckpoint persisted)
    {
        ValidateCheckpoint(persisted, requirePersistence: true);
        return new CustomerChatDispatchAuthorization(persisted.Authority, persisted.MessageId, persisted.AuthorityVersion, persisted.PersistenceReference);
    }

    public static CustomerChatCheckpoint ResolveReplay(CustomerChatCheckpoint persisted, CustomerChatCheckpoint replay)
    {
        ValidateCheckpoint(persisted, requirePersistence: true);
        ValidateCheckpoint(replay, requirePersistence: true);
        if (persisted.Authority != replay.Authority || persisted.MessageId != replay.MessageId)
            throw new UnauthorizedAccessException("Chat replay cannot cross authority or message identity.");
        if (persisted != replay)
            throw new InvalidOperationException("Conflicting durable chat evidence for the same message identity.");
        return persisted;
    }

    private static void ValidateCheckpoint(CustomerChatCheckpoint checkpoint, bool requirePersistence)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        checkpoint.Authority.Validate();
        if (checkpoint.MessageId == Guid.Empty || checkpoint.AuthorityVersion <= 0 || string.IsNullOrWhiteSpace(checkpoint.Content))
            throw new InvalidOperationException("Chat checkpoint is malformed.");
        if (checkpoint.AttachmentIds is null || checkpoint.AttachmentIds.Any(x => x == Guid.Empty) || checkpoint.AttachmentIds.Distinct().Count() != checkpoint.AttachmentIds.Count)
            throw new InvalidOperationException("Chat checkpoint attachment identities are malformed.");
        if (requirePersistence && string.IsNullOrWhiteSpace(checkpoint.PersistenceReference))
            throw new InvalidOperationException("Dispatch requires durable persistence evidence.");
        if (!string.IsNullOrEmpty(checkpoint.PersistenceReference)) RejectSecretMaterial(checkpoint.PersistenceReference);
    }

    private static string? NormalizeHint(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void RequireSameAuthority(CustomerChatAuthority expected, CustomerChatAuthority actual)
    {
        actual.Validate();
        if (expected != actual)
            throw new UnauthorizedAccessException("Customer chat ingress cannot cross tenant/company/user/conversation authority.");
    }

    private static void RejectSecretMaterial(string value)
    {
        if (value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("accountkey=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("connection string", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Customer chat contracts accept opaque references only.");
    }
}