namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class CustomerSlaPolicyRevisionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public long PolicyVersion { get; set; }
    public long AuthorityVersion { get; set; }
    public string ServiceLabel { get; set; } = string.Empty;
    public int SchedulerPriority { get; set; }
    public int PriorityCeiling { get; set; }
    public DateTimeOffset EffectiveAtUtc { get; set; }
}
