using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupBatchAllocationModelConfiguration
{
    internal static void ConfigureGroupBatchAllocation(this ModelBuilder model)
    {
        model.Entity<GroupBatchAllocationRecord>(entity =>
        {
            entity.ToTable("GroupBatchAllocations", table =>
            {
                table.HasCheckConstraint("CK_GroupBatchAllocations_Range", "[AfterSequence] >= 0 AND [AllocatedThroughSequence] > [AfterSequence] AND [ObservedCommittedThroughSequence] >= [AllocatedThroughSequence] AND [RawRevisionCount] BETWEEN 1 AND 500 AND [AllocatedThroughSequence]-[AfterSequence]=[RawRevisionCount]");
                table.HasCheckConstraint("CK_GroupBatchAllocations_Authority", "[SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [AccountVersion] > 0 AND [CredentialEpoch] > 0 AND [GrantVersion] > 0 AND DATEPART(tz,[AllocatedAtUtc]) = 0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBindingRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.BindingId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.AfterSequence }).IsUnique();
        });
        model.Entity<GroupBatchAllocatedRevisionRecord>(entity =>
        {
            entity.ToTable("GroupBatchAllocatedRevisions", table => table.HasCheckConstraint("CK_GroupBatchAllocatedRevisions_Values",
                "[CommittedSequence] > 0 AND [Revision] > 0 AND [SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [Kind] IN (1,2,3,4) AND DATEPART(tz,[CommittedAtUtc]) = 0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.CommittedSequence });
            entity.Property(x => x.ContentSha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchAllocationRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.BatchId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupMessageRevisionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.CommittedSequence }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.Revision }).IsUnique();
        });
    }
}
