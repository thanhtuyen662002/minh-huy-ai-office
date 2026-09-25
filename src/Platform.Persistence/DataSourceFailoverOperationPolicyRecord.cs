using MinhHuy.AIOffice.Shared.Contracts.Erp;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class DataSourceFailoverOperationPolicyRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid DataSourceId { get; set; }
    public DataSourceOperationKind Operation { get; set; }
    public long Version { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public DataSourceFailoverOperationPolicy ToContract()
        => new(TenantId, CompanyId, DataSourceId, Operation, Version, EffectiveAt);
}
