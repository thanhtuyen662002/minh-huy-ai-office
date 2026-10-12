using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupAutomaticInputPlanModelConfiguration
{
    internal static void ConfigureGroupAutomaticInputPlans(this ModelBuilder model)
    {
        model.Entity<GroupAutomaticInputPlanRecord>(entity =>
        {
            entity.ToTable("GroupAutomaticInputPlans", table =>
            {
                table.HasCheckConstraint("CK_GroupAutomaticInputPlans_Format", "[FormatVersion]=2 AND DATALENGTH([Plan]) BETWEEN 256 AND 32000");
                table.HasCheckConstraint("CK_GroupAutomaticInputPlans_Identity",
                    "[OriginalClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [OriginalClaimEpoch]>0 AND DATALENGTH([PlanSha256])=64 AND [PlanSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint("CK_GroupAutomaticInputPlans_Time", "DATEPART(tz,[CreatedAtUtc])=0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
            entity.Property(x => x.Plan).HasMaxLength(GroupAutomaticInputPlan.MaximumBytes);
            entity.Property(x => x.PlanSha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.HasOne<GroupBatchAllocationRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.BatchId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchClaimReceiptRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, OperationId = x.OriginalClaimOperationId, x.BatchId, Epoch = x.OriginalClaimEpoch })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId, x.BatchId, x.Epoch })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupAutomaticInputPlans_OriginalClaim");
        });
    }
}
