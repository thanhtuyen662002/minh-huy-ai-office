using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class CompanyAdministratorAuditRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid OperationId { get; set; }
    public string RequestHash { get; set; } = "";
    public bool BeforeAdministrator { get; set; }
    public bool AfterAdministrator { get; set; }
    public string BeforeRolesJson { get; set; } = "";
    public string AfterRolesJson { get; set; } = "";
    public long BeforeVersion { get; set; }
    public long AfterVersion { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

internal static class CompanyAdministratorAuditMapping
{
    public static void ConfigureCompanyAdministratorAudit(this ModelBuilder model) =>
        model.Entity<CompanyAdministratorAuditRecord>(entity =>
        {
            entity.ToTable("CompanyAdministratorAudits", table =>
                table.HasCheckConstraint("CK_CompanyAdministratorAudits_Versions", "[BeforeVersion] > 0 AND [AfterVersion] >= [BeforeVersion]"));
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.Property(row => row.RequestHash).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.OperationId }).IsUnique();
            entity.HasOne<CompanyMembershipRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, UserId = row.TargetUserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, Id = row.ActorUserId }).OnDelete(DeleteBehavior.Restrict);
        });
}
