using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupBatchTerminalModelConfiguration
{
    internal static void ConfigureGroupBatchTerminal(this ModelBuilder model)
    {
        model.Entity<GroupBatchTerminalReceiptRecord>(entity =>
        {
            entity.ToTable("GroupBatchTerminalReceipts", table =>
            {
                table.HasCheckConstraint("CK_GroupBatchTerminalReceipts_Manifest",
                    "[ManifestVersion]=1 AND DATALENGTH([Manifest]) BETWEEN 257 AND 8177 AND SUBSTRING([Manifest],1,8)=0x41494F4754524D31 AND DATALENGTH([ManifestSha256])=32 AND HASHBYTES('SHA2_256',[Manifest])=[ManifestSha256]");
                table.HasCheckConstraint("CK_GroupBatchTerminalReceipts_Range",
                    "[AfterSequence]>=0 AND [ThroughSequence]>[AfterSequence] AND [ThroughSequence]-[AfterSequence]=[RawRevisionCount] AND [RawRevisionCount] BETWEEN 1 AND 500 AND [SelectedMessageCount] BETWEEN 1 AND 100 AND [SelectedMessageCount]<=[RawRevisionCount] AND [ContributorCount] BETWEEN 1 AND [SelectedMessageCount] AND [NoteCount] BETWEEN 0 AND [ContributorCount]*40");
                table.HasCheckConstraint("CK_GroupBatchTerminalReceipts_Authority",
                    "[OperationId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimOwnerId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimEpoch]>0 AND [CredentialEpoch]>0 AND [GrantVersion]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [AccountVersion]>0 AND DATEPART(tz,[CommittedAtUtc])=0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
            // SQL Server limits varbinary(n) to 8000. Keep the 8177-byte logical
            // bound in the model and CHECK while using a legal physical type.
            entity.Property(x => x.Manifest).HasMaxLength(GroupBatchTerminalManifest.MaximumBytes).HasColumnType("varbinary(max)");
            entity.Property(x => x.ManifestSha256).HasMaxLength(32).HasColumnType("varbinary(32)");
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBindingRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.BindingId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchAllocationRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.BatchId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
            // A scoped original acquisition from another batch cannot satisfy
            // the receipt FK, even if it has a well-shaped epoch/nonce.
            entity.HasOne<GroupBatchClaimReceiptRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, OperationId = x.ClaimOperationId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.AfterSequence }).IsUnique();
        });
        model.Entity<GroupTerminalFrontierStateRecord>(entity =>
        {
            entity.ToTable("GroupTerminalFrontierStates", table => table.HasCheckConstraint("CK_GroupTerminalFrontierStates_Values",
                "[ThroughSequence]>=0 AND [Version]>0 AND (([ThroughSequence]=0 AND [LastTerminalBatchId] IS NULL) OR ([ThroughSequence]>0 AND [LastTerminalBatchId] IS NOT NULL)) AND DATEPART(tz,[UpdatedAtUtc])=0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId });
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBindingRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.BindingId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupSourceStateRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchTerminalReceiptRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, BatchId = x.LastTerminalBatchId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
