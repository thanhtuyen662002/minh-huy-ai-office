namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class BotChannelRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Channel { get; set; }
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public string SummaryScheduleJson { get; set; } = "[]";
    public string? TechGroupExternalId { get; set; }
    public long ConfigVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class BotCustomerGroupRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Channel { get; set; }
    public required string ExternalId { get; set; }
    public required string DisplayName { get; set; }
    public required string CustomerName { get; set; }
}

public sealed class BotAiSettingsRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public byte[]? ProtectedApiKey { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class BotStatusRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Channel { get; set; }
    public required string LoginState { get; set; }
    public string GroupsJson { get; set; } = "[]";
    public DateTimeOffset? LastSummaryAt { get; set; }
    public string? LastError { get; set; }
    public byte[]? LoginQrImage { get; set; }
    public string? LoginQrContentType { get; set; }
    public DateTimeOffset? LoginQrExpiresAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class BotCommandRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Channel { get; set; }
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}

public sealed class BotChannelAuditRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string? Channel { get; set; }
    public Guid ActorUserId { get; set; }
    public required string Field { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
