using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupBatchClaimModelConfiguration
{
    internal static void ConfigureGroupBatchClaims(this ModelBuilder model)
    {
        model.Entity<GroupBatchClaimStateRecord>(entity =>
        {
            entity.ToTable("GroupBatchClaimStates", table => table.HasCheckConstraint("CK_GroupBatchClaimStates_Lease",
                "[Epoch] > 0 AND [OwnerId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [ExpiresAtUtc] > [IssuedAtUtc] AND DATEPART(tz,[IssuedAtUtc]) = 0 AND DATEPART(tz,[ExpiresAtUtc]) = 0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchAllocationRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.BatchId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupBatchClaimReceiptRecord>(entity =>
        {
            entity.ToTable("GroupBatchClaimReceipts", table =>
            {
                table.HasCheckConstraint("CK_GroupBatchClaimReceipts_Lease", "[Epoch] > 0 AND [RequestedLifetimeTicks] BETWEEN 100000000 AND 6000000000 AND [ExpiresAtUtc] > [IssuedAtUtc] AND DATEPART(tz,[IssuedAtUtc]) = 0 AND DATEPART(tz,[ExpiresAtUtc]) = 0");
                table.HasCheckConstraint("CK_GroupBatchClaimReceipts_Authority", "[CredentialEpoch] > 0 AND [GrantVersion] > 0 AND [SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [AccountVersion] > 0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId });
            entity.Property(x => x.AuthoritySha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchAllocationRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.BatchId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.Epoch }).IsUnique();
        });
    }
}
