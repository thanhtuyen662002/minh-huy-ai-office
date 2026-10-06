namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>Operator-owned authority; never populated from customer source rows.</summary>
public sealed class DataSourceSecretBindingRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public required string CanonicalReference { get; set; }
    public required string Label { get; set; }
    public bool IsEnabled { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
