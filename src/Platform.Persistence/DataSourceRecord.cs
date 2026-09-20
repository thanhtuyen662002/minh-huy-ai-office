namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class DataSourceRecord
{
    public const int MaximumSecretReferenceLength = 512;

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }

    public required string LogicalName { get; set; }

    public required string Kind { get; set; }

    public required string Environment { get; set; }

    public required string Purpose { get; set; }

    public required string ConnectionSecretReference { get; set; }

    public bool AllowRead { get; set; } = true;

    public bool AllowWrite { get; set; }

    public int MaxConcurrency { get; set; } = 1;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
