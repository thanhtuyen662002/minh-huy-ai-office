using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupOriginalInvalidationModelConfiguration
{
    internal static void ConfigureGroupOriginalInvalidations(this ModelBuilder model)
    {
        model.Entity<GroupOriginalInvalidationRecord>(entity =>
        {
            entity.ToTable("GroupOriginalInvalidations", table =>
            {
                table.HasCheckConstraint("CK_GroupOriginalInvalidations_Format", "[FormatVersion]=1");
                table.HasCheckConstraint("CK_GroupOriginalInvalidations_Claims",
                    "[OriginalClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimOperationId]<>'00000000-0000-0000-0000-000000000000' " +
                    "AND [OriginalClaimEpoch]>0 AND [ClaimEpoch]>=[OriginalClaimEpoch] AND ([ClaimEpoch]>[OriginalClaimEpoch] OR [ClaimOperationId]=[OriginalClaimOperationId])");
                table.HasCheckConstraint("CK_GroupOriginalInvalidations_Hashes", string.Join(" AND ",
                    new[] { "PlanSha256", "ObservedDependenciesSha256", "CheckpointSha256" }.Select(name =>
                        $"DATALENGTH([{name}])=64 AND [{name}] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2")));
                table.HasCheckConstraint("CK_GroupOriginalInvalidations_Time", "DATEPART(tz,[CreatedAtUtc])=0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
            foreach (var name in new[] { "PlanSha256", "ObservedDependenciesSha256", "CheckpointSha256" })
                entity.Property<string>(name).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.HasOne<GroupAutomaticInputPlanRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupOriginalInvalidations_OriginalPlan");
            entity.HasOne<GroupBatchClaimReceiptRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, OperationId = x.OriginalClaimOperationId, x.BatchId, Epoch = x.OriginalClaimEpoch })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId, x.BatchId, x.Epoch })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupOriginalInvalidations_OriginalClaim");
            entity.HasOne<GroupBatchClaimReceiptRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, OperationId = x.ClaimOperationId, x.BatchId, Epoch = x.ClaimEpoch })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId, x.BatchId, x.Epoch })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupOriginalInvalidations_CheckpointClaim");
        });
    }
}
