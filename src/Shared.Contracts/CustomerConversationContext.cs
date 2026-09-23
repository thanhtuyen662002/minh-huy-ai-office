namespace MinhHuyAiOffice.Shared.Contracts;

public enum CustomerConversationMessageRole
{
    Customer = 1,
    Assistant = 2
}

public sealed record CustomerConversationContextAttachment(
    CustomerChatAuthority Authority,
    Guid AttachmentId,
    string ObjectReference);

public sealed record CustomerConversationContextMessage(
    CustomerChatAuthority Authority,
    long AuthorityVersion,
    Guid MessageId,
    Guid? InReplyToMessageId,
    DateTimeOffset OccurredAt,
    CustomerConversationMessageRole Role,
    string Content,
    IReadOnlyList<CustomerConversationContextAttachment> Attachments,
    string PersistenceReference,
    string? Model = null,
    string? Provider = null,
    string? Worker = null);

public sealed record CustomerConversationContextEntry(
    Guid MessageId,
    Guid? InReplyToMessageId,
    DateTimeOffset OccurredAt,
    CustomerConversationMessageRole Role,
    string Content,
    IReadOnlyList<Guid> AttachmentIds,
    string PersistenceReference);

public sealed record CustomerConversationContextSnapshot(
    CustomerChatAuthority Authority,
    long AuthorityVersion,
    Guid SelectedMessageId,
    IReadOnlyList<CustomerConversationContextEntry> Messages);

public static class CustomerConversationContext
{
    public static CustomerConversationContextSnapshot Assemble(
        CustomerChatAuthority trustedAuthority,
        long trustedAuthorityVersion,
        Guid selectedMessageId,
        int maxMessages,
        IReadOnlyList<CustomerConversationContextMessage> durableMessages)
    {
        trustedAuthority.Validate();
        if (trustedAuthorityVersion <= 0)
            throw new InvalidOperationException("Trusted authority version must be positive.");
        if (selectedMessageId == Guid.Empty)
            throw new InvalidOperationException("Selected message identity is required.");
        if (maxMessages <= 0)
            throw new InvalidOperationException("Context window must contain at least one message.");
        if (durableMessages is null)
            throw new ArgumentNullException(nameof(durableMessages));

        var byId = new Dictionary<Guid, CustomerConversationContextMessage>();
        foreach (var message in durableMessages)
        {
            ValidateMessage(trustedAuthority, trustedAuthorityVersion, message);
            if (byId.TryGetValue(message.MessageId, out var existing))
            {
                if (!Equivalent(existing, message))
                    throw new InvalidOperationException("Conflicting durable conversation evidence for the same message identity.");
                continue;
            }
            byId.Add(message.MessageId, message);
        }

        if (!byId.ContainsKey(selectedMessageId))
            throw new InvalidOperationException("Selected durable message is not present in the authorized conversation evidence.");

        var required = new HashSet<Guid>();
        var cursor = selectedMessageId;
        while (true)
        {
            if (!required.Add(cursor))
                throw new InvalidOperationException("Conversation reply ancestry contains a cycle.");
            var current = byId[cursor];
            if (current.InReplyToMessageId is not Guid parentId)
                break;
            if (!byId.ContainsKey(parentId))
                throw new InvalidOperationException("Conversation reply ancestry is incomplete.");
            cursor = parentId;
        }

        if (required.Count > maxMessages)
            throw new InvalidOperationException("Context window is too small to preserve selected message ancestry.");

        var ordered = byId.Values
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.MessageId)
            .ToArray();
        var selected = new HashSet<Guid>(required);
        foreach (var candidate in ordered.Reverse())
        {
            if (selected.Count >= maxMessages)
                break;
            selected.Add(candidate.MessageId);
        }

        var entries = ordered
            .Where(x => selected.Contains(x.MessageId))
            .Select(x => new CustomerConversationContextEntry(
                x.MessageId,
                x.InReplyToMessageId,
                x.OccurredAt,
                x.Role,
                x.Content,
                x.Attachments.Select(a => a.AttachmentId).Order().ToArray(),
                x.PersistenceReference))
            .ToArray();

        return new CustomerConversationContextSnapshot(trustedAuthority, trustedAuthorityVersion, selectedMessageId, entries);
    }

    private static void ValidateMessage(CustomerChatAuthority authority, long authorityVersion, CustomerConversationContextMessage message)
    {
        if (message is null)
            throw new InvalidOperationException("Durable conversation message is required.");
        message.Authority.Validate();
        if (message.Authority != authority)
            throw new UnauthorizedAccessException("Conversation context cannot cross tenant/company/user/conversation authority.");
        if (message.AuthorityVersion != authorityVersion)
            throw new UnauthorizedAccessException("Conversation context authority evidence is stale or unrecognized.");
        if (message.MessageId == Guid.Empty || message.InReplyToMessageId == Guid.Empty || message.OccurredAt.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(message.Role) || string.IsNullOrWhiteSpace(message.Content) || string.IsNullOrWhiteSpace(message.PersistenceReference))
            throw new InvalidOperationException("Durable conversation message evidence is malformed.");
        RejectSecretMaterial(message.PersistenceReference);
        if (message.Attachments is null)
            throw new InvalidOperationException("Conversation attachment collection is required.");

        var attachmentIds = new HashSet<Guid>();
        foreach (var attachment in message.Attachments)
        {
            if (attachment is null || attachment.Authority != authority || attachment.AttachmentId == Guid.Empty || string.IsNullOrWhiteSpace(attachment.ObjectReference))
                throw new UnauthorizedAccessException("Conversation attachment is malformed or crosses authority.");
            RejectSecretMaterial(attachment.ObjectReference);
            if (!attachmentIds.Add(attachment.AttachmentId))
                throw new InvalidOperationException("Duplicate conversation attachment identity is ambiguous.");
        }
    }

    private static bool Equivalent(CustomerConversationContextMessage left, CustomerConversationContextMessage right) =>
        left.Authority == right.Authority && left.AuthorityVersion == right.AuthorityVersion && left.MessageId == right.MessageId &&
        left.InReplyToMessageId == right.InReplyToMessageId && left.OccurredAt == right.OccurredAt && left.Role == right.Role &&
        left.Content == right.Content && left.PersistenceReference == right.PersistenceReference &&
        left.Attachments.OrderBy(x => x.AttachmentId).SequenceEqual(right.Attachments.OrderBy(x => x.AttachmentId));

    private static void RejectSecretMaterial(string value)
    {
        if (value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("accountkey=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("connection string", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Conversation context accepts opaque references only.");
    }
}
