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

public sealed record CustomerChatIngressRequest(CustomerChatAuthority Authority, Guid MessageId, long AuthorityVersion, string Content, IReadOnlyList<CustomerChatAttachment> Attachments, string? RequestedAgentRole = null, string? RequestedModel = null, string? RequestedProvider = null);
public sealed record CustomerChatCheckpoint(CustomerChatAuthority Authority, Guid MessageId, long AuthorityVersion, string Content, IReadOnlyList<Guid> AttachmentIds, string? RequestedAgentRole, string? RequestedModel, string? RequestedProvider, string PersistenceReference);
public sealed record CustomerChatDispatchAuthorization(CustomerChatAuthority Authority, Guid MessageId, long AuthorityVersion, string PersistenceReference);

public static class CustomerChatIngress
{
    public static CustomerChatCheckpoint Prepare(CustomerChatAuthority trustedAuthority, long trustedAuthorityVersion, CustomerChatIngressRequest request)
    {
        trustedAuthority.Validate();
        if (trustedAuthorityVersion <= 0)
            throw new InvalidOperationException("Trusted authority version must be positive.");
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        RequireSameAuthority(trustedAuthority, request.Authority);
        if (request.AuthorityVersion != trustedAuthorityVersion)
            throw new UnauthorizedAccessException("Customer chat authority evidence is stale or unrecognized.");
        if (request.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.Content))
            throw new InvalidOperationException("Chat ingress requires durable message identity and content.");
        if (request.Attachments is null)
            throw new InvalidOperationException("Attachment collection is required.");

        var attachmentIds = new HashSet<Guid>();
        foreach (var attachment in request.Attachments)
        {
            if (attachment is null)
                throw new InvalidOperationException("Attachment is required.");
            RequireSameAuthority(trustedAuthority, attachment.Authority);
            if (attachment.AttachmentId == Guid.Empty || string.IsNullOrWhiteSpace(attachment.ObjectReference))
                throw new InvalidOperationException("Attachment requires durable identity and opaque object reference.");
            RejectSecretMaterial(attachment.ObjectReference);
            if (!attachmentIds.Add(attachment.AttachmentId))
                throw new InvalidOperationException("Duplicate attachment identity is ambiguous.");
        }

        return new CustomerChatCheckpoint(trustedAuthority, request.MessageId, trustedAuthorityVersion, request.Content, attachmentIds.Order().ToArray(), NormalizeHint(request.RequestedAgentRole), NormalizeHint(request.RequestedModel), NormalizeHint(request.RequestedProvider), string.Empty);
    }

    public static CustomerChatCheckpoint MarkPersisted(CustomerChatCheckpoint prepared, string persistenceReference)
    {
        ValidateCheckpoint(prepared, false);
        if (string.IsNullOrWhiteSpace(persistenceReference))
            throw new InvalidOperationException("Durable persistence evidence is required before dispatch.");
        RejectSecretMaterial(persistenceReference);
        return prepared with { PersistenceReference = persistenceReference };
    }

    public static CustomerChatDispatchAuthorization AuthorizeDispatch(CustomerChatCheckpoint persisted)
    {
        ValidateCheckpoint(persisted, true);
        return new CustomerChatDispatchAuthorization(persisted.Authority, persisted.MessageId, persisted.AuthorityVersion, persisted.PersistenceReference);
    }

    public static CustomerChatCheckpoint ResolveReplay(CustomerChatCheckpoint persisted, CustomerChatCheckpoint replay)
    {
        ValidateCheckpoint(persisted, true);
        ValidateCheckpoint(replay, true);
        if (persisted.Authority != replay.Authority || persisted.MessageId != replay.MessageId)
            throw new UnauthorizedAccessException("Chat replay cannot cross authority or message identity.");
        if (!Equivalent(persisted, replay))
            throw new InvalidOperationException("Conflicting durable chat evidence for the same message identity.");
        return persisted;
    }

    private static bool Equivalent(CustomerChatCheckpoint left, CustomerChatCheckpoint right) =>
        left.Authority == right.Authority && left.MessageId == right.MessageId && left.AuthorityVersion == right.AuthorityVersion &&
        left.Content == right.Content && left.AttachmentIds.SequenceEqual(right.AttachmentIds) &&
        left.RequestedAgentRole == right.RequestedAgentRole && left.RequestedModel == right.RequestedModel &&
        left.RequestedProvider == right.RequestedProvider && left.PersistenceReference == right.PersistenceReference;

    private static void ValidateCheckpoint(CustomerChatCheckpoint checkpoint, bool requirePersistence)
    {
        if (checkpoint is null)
            throw new ArgumentNullException(nameof(checkpoint));
        checkpoint.Authority.Validate();
        if (checkpoint.MessageId == Guid.Empty || checkpoint.AuthorityVersion <= 0 || string.IsNullOrWhiteSpace(checkpoint.Content))
            throw new InvalidOperationException("Chat checkpoint is malformed.");
        if (checkpoint.AttachmentIds is null || checkpoint.AttachmentIds.Any(x => x == Guid.Empty) || checkpoint.AttachmentIds.Distinct().Count() != checkpoint.AttachmentIds.Count)
            throw new InvalidOperationException("Chat checkpoint attachment identities are malformed.");
        if (requirePersistence && string.IsNullOrWhiteSpace(checkpoint.PersistenceReference))
            throw new InvalidOperationException("Dispatch requires durable persistence evidence.");
        if (!string.IsNullOrEmpty(checkpoint.PersistenceReference))
            RejectSecretMaterial(checkpoint.PersistenceReference);
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
        if (value.Contains("password=", StringComparison.OrdinalIgnoreCase) || value.Contains("secret=", StringComparison.OrdinalIgnoreCase) || value.Contains("accountkey=", StringComparison.OrdinalIgnoreCase) || value.Contains("connection string", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Customer chat contracts accept opaque references only.");
    }
}
