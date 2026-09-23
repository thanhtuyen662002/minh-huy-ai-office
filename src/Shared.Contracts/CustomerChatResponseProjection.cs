namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerChatResponseAttachment(CustomerChatAuthority Authority, Guid AttachmentId, string ObjectReference);

public sealed record CustomerChatResponseEvidence(
    CustomerChatAuthority Authority,
    long AuthorityVersion,
    Guid MessageId,
    Guid InReplyToMessageId,
    Guid TaskId,
    string Content,
    IReadOnlyList<CustomerChatResponseAttachment> Attachments,
    string? Model = null,
    string? Provider = null,
    string? Worker = null);

public sealed record CustomerChatResponseCheckpoint(
    CustomerChatAuthority Authority,
    long AuthorityVersion,
    Guid MessageId,
    Guid InReplyToMessageId,
    Guid TaskId,
    string Content,
    IReadOnlyList<Guid> AttachmentIds,
    string? Model,
    string? Provider,
    string? Worker,
    string PersistenceReference);

public sealed record CustomerChatDeliveryAuthorization(
    CustomerChatAuthority Authority,
    long AuthorityVersion,
    Guid MessageId,
    Guid InReplyToMessageId,
    Guid TaskId,
    string PersistenceReference);

public static class CustomerChatResponseProjection
{
    public static CustomerChatResponseCheckpoint Prepare(
        CustomerChatAuthority trustedAuthority,
        long trustedAuthorityVersion,
        CustomerChatResponseEvidence evidence)
    {
        trustedAuthority.Validate();
        if (trustedAuthorityVersion <= 0)
            throw new InvalidOperationException("Trusted authority version must be positive.");
        if (evidence is null)
            throw new ArgumentNullException(nameof(evidence));

        RequireSameAuthority(trustedAuthority, evidence.Authority);
        if (evidence.AuthorityVersion != trustedAuthorityVersion)
            throw new UnauthorizedAccessException("Customer chat response authority evidence is stale or unrecognized.");
        if (evidence.MessageId == Guid.Empty || evidence.InReplyToMessageId == Guid.Empty || evidence.TaskId == Guid.Empty || string.IsNullOrWhiteSpace(evidence.Content))
            throw new InvalidOperationException("Chat response requires durable message, reply, task identity and content.");
        if (evidence.Attachments is null)
            throw new InvalidOperationException("Response attachment collection is required.");

        var attachmentIds = new HashSet<Guid>();
        foreach (var attachment in evidence.Attachments)
        {
            if (attachment is null)
                throw new InvalidOperationException("Response attachment is required.");
            RequireSameAuthority(trustedAuthority, attachment.Authority);
            if (attachment.AttachmentId == Guid.Empty || string.IsNullOrWhiteSpace(attachment.ObjectReference))
                throw new InvalidOperationException("Response attachment requires durable identity and opaque object reference.");
            RejectSecretMaterial(attachment.ObjectReference);
            if (!attachmentIds.Add(attachment.AttachmentId))
                throw new InvalidOperationException("Duplicate response attachment identity is ambiguous.");
        }

        return new CustomerChatResponseCheckpoint(
            trustedAuthority,
            trustedAuthorityVersion,
            evidence.MessageId,
            evidence.InReplyToMessageId,
            evidence.TaskId,
            evidence.Content,
            attachmentIds.Order().ToArray(),
            NormalizeDiagnostic(evidence.Model),
            NormalizeDiagnostic(evidence.Provider),
            NormalizeDiagnostic(evidence.Worker),
            string.Empty);
    }

    public static CustomerChatResponseCheckpoint MarkPersisted(CustomerChatResponseCheckpoint prepared, string persistenceReference)
    {
        ValidateCheckpoint(prepared, false);
        if (string.IsNullOrWhiteSpace(persistenceReference))
            throw new InvalidOperationException("Durable response persistence evidence is required before delivery.");
        RejectSecretMaterial(persistenceReference);
        return prepared with { PersistenceReference = persistenceReference };
    }

    public static CustomerChatDeliveryAuthorization AuthorizeDelivery(
        CustomerChatAuthority trustedAuthority,
        long trustedAuthorityVersion,
        CustomerChatResponseCheckpoint persisted)
    {
        trustedAuthority.Validate();
        if (trustedAuthorityVersion <= 0)
            throw new InvalidOperationException("Trusted authority version must be positive.");
        ValidateCheckpoint(persisted, true);
        RequireSameAuthority(trustedAuthority, persisted.Authority);
        if (persisted.AuthorityVersion != trustedAuthorityVersion)
            throw new UnauthorizedAccessException("Stale chat response authority cannot be delivered.");

        return new CustomerChatDeliveryAuthorization(
            persisted.Authority,
            persisted.AuthorityVersion,
            persisted.MessageId,
            persisted.InReplyToMessageId,
            persisted.TaskId,
            persisted.PersistenceReference);
    }

    public static CustomerChatResponseCheckpoint ResolveReplay(CustomerChatResponseCheckpoint persisted, CustomerChatResponseCheckpoint replay)
    {
        ValidateCheckpoint(persisted, true);
        ValidateCheckpoint(replay, true);
        if (persisted.Authority != replay.Authority || persisted.MessageId != replay.MessageId || persisted.TaskId != replay.TaskId)
            throw new UnauthorizedAccessException("Chat response replay cannot cross authority or durable response identity.");
        if (!Equivalent(persisted, replay))
            throw new InvalidOperationException("Conflicting durable chat response evidence for the same response identity.");
        return persisted;
    }

    private static bool Equivalent(CustomerChatResponseCheckpoint left, CustomerChatResponseCheckpoint right) =>
        left.Authority == right.Authority && left.AuthorityVersion == right.AuthorityVersion &&
        left.MessageId == right.MessageId && left.InReplyToMessageId == right.InReplyToMessageId && left.TaskId == right.TaskId &&
        left.Content == right.Content && left.AttachmentIds.SequenceEqual(right.AttachmentIds) &&
        left.Model == right.Model && left.Provider == right.Provider && left.Worker == right.Worker &&
        left.PersistenceReference == right.PersistenceReference;

    private static void ValidateCheckpoint(CustomerChatResponseCheckpoint checkpoint, bool requirePersistence)
    {
        if (checkpoint is null)
            throw new ArgumentNullException(nameof(checkpoint));
        checkpoint.Authority.Validate();
        if (checkpoint.AuthorityVersion <= 0 || checkpoint.MessageId == Guid.Empty || checkpoint.InReplyToMessageId == Guid.Empty || checkpoint.TaskId == Guid.Empty || string.IsNullOrWhiteSpace(checkpoint.Content))
            throw new InvalidOperationException("Chat response checkpoint is malformed.");
        if (checkpoint.AttachmentIds is null || checkpoint.AttachmentIds.Any(x => x == Guid.Empty) || checkpoint.AttachmentIds.Distinct().Count() != checkpoint.AttachmentIds.Count)
            throw new InvalidOperationException("Chat response attachment identities are malformed.");
        if (requirePersistence && string.IsNullOrWhiteSpace(checkpoint.PersistenceReference))
            throw new InvalidOperationException("Delivery requires durable response persistence evidence.");
        if (!string.IsNullOrEmpty(checkpoint.PersistenceReference))
            RejectSecretMaterial(checkpoint.PersistenceReference);
    }

    private static string? NormalizeDiagnostic(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void RequireSameAuthority(CustomerChatAuthority expected, CustomerChatAuthority actual)
    {
        actual.Validate();
        if (expected != actual)
            throw new UnauthorizedAccessException("Customer chat response cannot cross tenant/company/user/conversation authority.");
    }

    private static void RejectSecretMaterial(string value)
    {
        if (value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("accountkey=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("connection string", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Customer chat response contracts accept opaque references only.");
    }
}
