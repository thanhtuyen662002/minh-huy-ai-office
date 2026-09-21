namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record ConversationIngestionScope(
    string TenantId,
    string CompanyId,
    string TaskId,
    string ConversationId);

public sealed record AttachmentManifest(
    string AttachmentId,
    string MessageId,
    string ContentType,
    long SizeBytes,
    string ContentDigest,
    string ContentReference);

public sealed record ConversationMessageManifest(
    string MessageId,
    ConversationIngestionScope Scope,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string? Text,
    IReadOnlyList<AttachmentManifest> Attachments);

public sealed record ConversationIngestionManifest(
    ConversationIngestionScope Scope,
    IReadOnlyList<ConversationMessageManifest> Messages);

public static class ConversationIngestion
{
    public static ConversationIngestionManifest Validate(
        ConversationIngestionScope scope,
        IEnumerable<ConversationMessageManifest> messages)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(messages);

        var materialized = messages.ToArray();
        if (materialized.Select(x => x.MessageId).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            throw new InvalidOperationException("Duplicate message identity cannot be ingested twice.");

        var attachmentIds = new HashSet<string>(StringComparer.Ordinal);
        long? previousSequence = null;
        foreach (var message in materialized.OrderBy(x => x.Sequence))
        {
            ValidateMessage(scope, message, attachmentIds);
            if (previousSequence is not null && message.Sequence <= previousSequence)
                throw new InvalidOperationException("Message sequence must be strictly increasing.");
            previousSequence = message.Sequence;
        }

        return new ConversationIngestionManifest(
            scope,
            materialized.OrderBy(x => x.Sequence).ToArray());
    }

    private static void ValidateMessage(
        ConversationIngestionScope expected,
        ConversationMessageManifest message,
        HashSet<string> attachmentIds)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateRequired(message.MessageId, nameof(message.MessageId));
        ValidateScope(message.Scope);
        if (!SameScope(expected, message.Scope))
            throw new InvalidOperationException("Cross-tenant/company/task/conversation messages cannot be combined.");
        if (message.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(message.Sequence));
        if (message.OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Message timestamps must be UTC.", nameof(message));
        ArgumentNullException.ThrowIfNull(message.Attachments);
        if (string.IsNullOrWhiteSpace(message.Text) && message.Attachments.Count == 0)
            throw new ArgumentException("A message must contain text or attachment metadata.", nameof(message));

        foreach (var attachment in message.Attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment);
            ValidateRequired(attachment.AttachmentId, nameof(attachment.AttachmentId));
            ValidateRequired(attachment.MessageId, nameof(attachment.MessageId));
            ValidateRequired(attachment.ContentType, nameof(attachment.ContentType));
            ValidateRequired(attachment.ContentDigest, nameof(attachment.ContentDigest));
            ValidateRequired(attachment.ContentReference, nameof(attachment.ContentReference));
            if (attachment.SizeBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(attachment.SizeBytes));
            if (!string.Equals(attachment.MessageId, message.MessageId, StringComparison.Ordinal))
                throw new InvalidOperationException("Attachment ownership must match its message identity.");
            if (!attachmentIds.Add(attachment.AttachmentId))
                throw new InvalidOperationException("Duplicate attachment identity cannot be ingested twice.");
        }
    }

    private static bool SameScope(ConversationIngestionScope expected, ConversationIngestionScope actual) =>
        string.Equals(expected.TenantId, actual.TenantId, StringComparison.Ordinal) &&
        string.Equals(expected.CompanyId, actual.CompanyId, StringComparison.Ordinal) &&
        string.Equals(expected.TaskId, actual.TaskId, StringComparison.Ordinal) &&
        string.Equals(expected.ConversationId, actual.ConversationId, StringComparison.Ordinal);

    private static void ValidateScope(ConversationIngestionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateRequired(scope.TenantId, nameof(scope.TenantId));
        ValidateRequired(scope.CompanyId, nameof(scope.CompanyId));
        ValidateRequired(scope.TaskId, nameof(scope.TaskId));
        ValidateRequired(scope.ConversationId, nameof(scope.ConversationId));
    }

    private static void ValidateRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be a canonical non-empty value.", name);
    }
}
