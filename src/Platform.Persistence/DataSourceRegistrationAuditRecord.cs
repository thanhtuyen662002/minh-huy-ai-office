using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class DataSourceRegistrationAuditRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid DataSourceId { get; set; }
    public Guid BindingId { get; set; }
    public long BindingVersion { get; set; }
    public Guid OperationId { get; set; }
    public string RequestHash { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

internal static class DataSourceRegistrationAuditMapping
{
    public static void ConfigureDataSourceRegistrationAudit(this ModelBuilder model)
    {
        model.Entity<DataSourceRegistrationAuditRecord>(entity =>
        {
            entity.ToTable("DataSourceRegistrationAudits", table =>
                table.HasCheckConstraint("CK_DataSourceRegistrationAudits_BindingVersion", "[BindingVersion] > 0"));
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.Property(row => row.RequestHash).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.OperationId }).IsUnique();
            entity.HasOne<DataSourceRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, Id = row.DataSourceId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, Id = row.ActorUserId }).OnDelete(DeleteBehavior.Restrict);
            // Historical binding identity survives operator grant deletion/recreation.
            // No FK to the mutable operator-owned grant table.
        });
    }
}
