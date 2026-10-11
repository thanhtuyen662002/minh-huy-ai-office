using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupWorkRawModelConfiguration
{
    internal static void ConfigureGroupWorkRawAccounting(this ModelBuilder model)
    {
        model.Entity<GroupWorkRawDispositionRecord>(entity =>
        {
            entity.ToTable("GroupWorkRawDispositions", table => table.HasCheckConstraint("CK_GroupWorkRawDispositions_Values",
                "[CommittedSequence]>0 AND [RawRevision]>0 AND [SelectedMessageRevision]>0 AND [Outcome] IN (1,2,3,4,5,6,7,8) AND (([Relation]=1 AND [RawRevision]=[SelectedMessageRevision]) OR ([Relation]=2 AND [RawRevision]<>[SelectedMessageRevision]))"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.CommittedSequence });
            entity.HasOne<GroupBatchAllocatedRevisionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.CommittedSequence }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupWorkSourceDispositionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.MessageId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupWorkCommitReceiptRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupMessageRevisionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, Revision = x.RawRevision }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId, x.CommittedSequence });
        });
    }
}
