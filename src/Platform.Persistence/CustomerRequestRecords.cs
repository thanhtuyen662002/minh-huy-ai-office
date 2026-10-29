namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class CustomerRequestRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string SourceGroupExternalId { get; set; }
    public required string SourceGroupName { get; set; }
    public required string CustomerName { get; set; }
    public required string ReportedBy { get; set; }
    public required string Summary { get; set; }
    public string? CustomerDesiredDueText { get; set; }
    public required string Status { get; set; }
    public string Origin { get; set; } = "ai_extracted";
    public Guid? AssigneeUserId { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset SourceWindowFrom { get; set; }
    public DateTimeOffset SourceWindowTo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public abstract class CustomerRequestChildRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid RequestId { get; set; }
    public int Ordinal { get; set; }
}

public sealed class CustomerRequestItemRecord : CustomerRequestChildRecord
{
    public required string Content { get; set; }
    public required string Priority { get; set; }
    public required string Kind { get; set; }
}

public sealed class CustomerRequestEvidenceRecord : CustomerRequestChildRecord
{
    public required string Quote { get; set; }
    public required string SourceMessageId { get; set; }
    public required string SenderName { get; set; }
    public DateTimeOffset MessageTimestamp { get; set; }
}

public sealed class CustomerRequestOpenQuestionRecord : CustomerRequestChildRecord
{
    public required string Content { get; set; }
}

public sealed class CustomerRequestEventRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    public required string ActorType { get; set; }
    public Guid ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? ActorExternalId { get; set; }
    public string? SourceMessageId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public sealed class CustomerRequestCounterRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public long LastNumber { get; set; }
}

public sealed class CustomerRequestNonceRecord
{
    public Guid ServiceId { get; set; }
    public Guid Nonce { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
