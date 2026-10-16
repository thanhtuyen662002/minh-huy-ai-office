using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupOriginalAdmissionSidecarModelConfiguration
{
    internal static void ConfigureGroupOriginalAdmissionSidecars(this ModelBuilder model)
    {
        model.Entity<GroupOriginalAdmissionSidecarRecord>(entity =>
        {
            entity.ToTable("GroupOriginalAdmissionSidecars", table =>
            {
                table.HasCheckConstraint("CK_GroupOriginalAdmissionSidecars_Format",
                    "[FormatVersion]=1 AND DATALENGTH([Sidecar]) BETWEEN 211 AND 1010000");
                table.HasCheckConstraint("CK_GroupOriginalAdmissionSidecars_Identity",
                    "[OriginalClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [OriginalClaimEpoch]>0 " +
                    "AND DATALENGTH([PlanSha256])=64 AND [PlanSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 " +
                    "AND DATALENGTH([SidecarSha256])=64 AND [SidecarSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint("CK_GroupOriginalAdmissionSidecars_Time", "DATEPART(tz,[CreatedAtUtc])=0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
            entity.Property(x => x.Sidecar).HasMaxLength(GroupOriginalAdmissionSidecar.MaximumBytes);
            entity.Property(x => x.PlanSha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.SidecarSha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.HasOne<GroupAutomaticInputPlanRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupOriginalAdmissionSidecars_OriginalPlan");
            entity.HasOne<GroupBatchClaimReceiptRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, OperationId = x.OriginalClaimOperationId, x.BatchId, Epoch = x.OriginalClaimEpoch })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId, x.BatchId, x.Epoch })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupOriginalAdmissionSidecars_OriginalClaim");
        });
    }
}
