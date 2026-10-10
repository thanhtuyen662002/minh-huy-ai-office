using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupWorkNoteModelConfiguration
{
    internal static void ConfigureGroupWorkNotes(this ModelBuilder model)
    {
        model.Entity<GroupWorkCommitReceiptRecord>(entity =>
        {
            entity.ToTable("GroupWorkCommitReceipts", table =>
            {
                table.HasCheckConstraint("CK_GroupWorkCommitReceipts_Counts", "[SelectedMessageCount] BETWEEN 1 AND 100 AND (([Outcome]=2 AND [NoteCount]=0) OR ([Outcome] IN (1,3) AND [NoteCount] BETWEEN 1 AND 40))");
                table.HasCheckConstraint("CK_GroupWorkCommitReceipts_Authority", "[ClaimEpoch]>0 AND [CredentialEpoch]>0 AND [GrantVersion]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [AccountVersion]>0 AND DATEPART(tz,[CommittedAtUtc])=0");
                table.HasCheckConstraint("CK_GroupWorkCommitReceipts_SourceSet", "DATALENGTH([SourceSetSha256])=64 AND [SourceSetSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId });
            Hash(entity.Property(x => x.SourceSetSha256));
            entity.HasOne<GroupBatchAllocationRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.BatchId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId }).IsUnique();
        });
        model.Entity<GroupCustomerRequestRecord>(entity =>
        {
            entity.ToTable("GroupCustomerRequests", table =>
            {
                table.HasCheckConstraint("CK_GroupCustomerRequests_Values", "[OriginCandidateOrdinal] BETWEEN 1 AND 40 AND [Kind] IN (1,2,3,4,5) AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [CurrentRevision]>0 AND [BusinessVersion]>0 AND [BusinessStatus] IN (1,2,3,4,5)");
                table.HasCheckConstraint("CK_GroupCustomerRequests_Code", "DATALENGTH([RequestCode])=36 AND LEFT([RequestCode],4)='REQ-' AND SUBSTRING([RequestCode],5,32) NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint("CK_GroupCustomerRequests_Times", "DATEPART(tz,[CreatedAtUtc])=0 AND DATEPART(tz,[UpdatedAtUtc])=0 AND [UpdatedAtUtc]>=[CreatedAtUtc] AND ([CommittedDueAtUtc] IS NULL OR DATEPART(tz,[CommittedDueAtUtc])=0) AND ([ConfirmedAtUtc] IS NULL OR (DATEPART(tz,[ConfirmedAtUtc])=0 AND [ConfirmedAtUtc]>=[CreatedAtUtc]))");
                table.HasCheckConstraint("CK_GroupCustomerRequests_ITConfirmation", "([ConfirmedByUserId] IS NOT NULL AND [ConfirmedAtUtc] IS NOT NULL) OR ([ConfirmedByUserId] IS NULL AND [ConfirmedAtUtc] IS NULL AND [BusinessStatus] IN (1,3) AND [AssignedToUserId] IS NULL AND [CommittedDueAtUtc] IS NULL)");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
            entity.Property(x => x.RequestCode).HasMaxLength(36).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.BusinessVersion).IsConcurrencyToken();
            entity.Property(x => x.CurrentRevision).IsConcurrencyToken();
            entity.HasOne<GroupWorkCommitReceiptRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, BatchId = x.OriginBatchId, OperationId = x.OriginOperationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, UserId = x.AssignedToUserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, UserId = x.ConfirmedByUserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestCode }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OriginBatchId, x.OriginOperationId, x.OriginCandidateOrdinal }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.DeletionGeneration, x.CreatedAtUtc, x.Id });
        });
        model.Entity<GroupRequestRevisionRecord>(entity =>
        {
            entity.ToTable("GroupRequestRevisions", table =>
            {
                PayloadChecks(table, "GroupRequestRevisions");
                table.HasCheckConstraint("CK_GroupRequestRevisions_Origin", "([Origin]=1 AND [VerificationLevel]=1 AND [AuthorServiceId] IS NOT NULL AND [AuthorUserId] IS NULL AND [SourceBatchId] IS NOT NULL AND [ClaimEpoch] IS NOT NULL AND [ClaimEpoch]>0) OR ([Origin]=2 AND [VerificationLevel]=2 AND [AuthorServiceId] IS NULL AND [AuthorUserId] IS NOT NULL AND [SourceBatchId] IS NULL AND [ClaimEpoch] IS NULL) OR ([Origin]=3 AND [VerificationLevel]=3 AND [AuthorServiceId] IS NOT NULL AND [AuthorUserId] IS NULL AND [SourceBatchId] IS NOT NULL AND [ClaimEpoch] IS NOT NULL AND [ClaimEpoch]>0)");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.Revision });
            Payload(entity.Property(x => x.ContentKeyId), entity.Property(x => x.ProtectedContent), entity.Property(x => x.EnvelopeSha256));
            entity.HasOne<GroupCustomerRequestRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.RequestId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.AuthorServiceId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, UserId = x.AuthorUserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchAllocationRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.SourceBatchId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupRequestEvidenceRecord>(entity =>
        {
            entity.ToTable("GroupRequestEvidence", table => table.HasCheckConstraint("CK_GroupRequestEvidence_Values", "[RequestRevision]>0 AND [Ordinal] BETWEEN 1 AND 10 AND [MessageRevision]>0 AND [Kind] IN (1,2)"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.RequestRevision, x.Ordinal });
            entity.HasOne<GroupRequestRevisionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, Revision = x.RequestRevision }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupMessageRevisionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, Revision = x.MessageRevision }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupWorkSourceDispositionRecord>(entity =>
        {
            entity.ToTable("GroupWorkSourceDispositions", table => table.HasCheckConstraint("CK_GroupWorkSourceDispositions_Values", "[MessageRevision]>0 AND [Outcome] IN (1,2,3,4,5,6,7,8)"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.MessageId });
            entity.HasOne<GroupWorkCommitReceiptRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupMessageRevisionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, Revision = x.MessageRevision }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupNotesCommittedOutboxRecord>(entity =>
        {
            entity.ToTable("GroupNotesCommittedOutbox", table => table.HasCheckConstraint("CK_GroupNotesCommittedOutbox_Values", "[NoteCount] BETWEEN 1 AND 40 AND [PublishAttempts]>=0 AND DATEPART(tz,[CommittedAtUtc])=0 AND DATEPART(tz,[AvailableAtUtc])=0 AND [AvailableAtUtc]>=[CommittedAtUtc] AND ([PublishedAtUtc] IS NULL OR (DATEPART(tz,[PublishedAtUtc])=0 AND [PublishedAtUtc]>=[CommittedAtUtc]))"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
            entity.HasOne<GroupWorkCommitReceiptRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.PublishedAtUtc, x.AvailableAtUtc });
        });
        model.Entity<GroupNotesCommittedItemRecord>(entity =>
        {
            entity.ToTable("GroupNotesCommittedItems", table => table.HasCheckConstraint("CK_GroupNotesCommittedItems_Values", "[Ordinal] BETWEEN 1 AND 40 AND [RequestRevision]>0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OutboxId, x.Ordinal });
            entity.HasOne<GroupNotesCommittedOutboxRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.OutboxId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupRequestRevisionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, Revision = x.RequestRevision }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OutboxId, x.RequestId, x.RequestRevision }).IsUnique();
        });
        model.Entity<GroupEditorGrantRecord>(entity =>
        {
            entity.ToTable("GroupEditorGrants", table => table.HasCheckConstraint("CK_GroupEditorGrants_Version", "[Version]>0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.UserId });
            entity.HasOne<GroupBindingRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.BindingId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupGlossaryEntryRecord>(entity =>
        {
            entity.ToTable("GroupGlossaryEntries", table => table.HasCheckConstraint("CK_GroupGlossaryEntries_Values", "[CurrentRevision]>0 AND [Version]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND DATEPART(tz,[CreatedAtUtc])=0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
            entity.HasOne<GroupBindingRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.BindingId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, UserId = x.PublishedByUserId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupGlossaryRevisionRecord>(entity =>
        {
            entity.ToTable("GroupGlossaryRevisions", table => PayloadChecks(table, "GroupGlossaryRevisions"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.EntryId, x.Revision });
            Payload(entity.Property(x => x.ContentKeyId), entity.Property(x => x.ProtectedContent), entity.Property(x => x.EnvelopeSha256));
            entity.HasOne<GroupGlossaryEntryRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.EntryId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, UserId = x.PublishedByUserId }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void Hash(PropertyBuilder<string> hash) => hash.HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
    private static void Payload(PropertyBuilder<string> key, PropertyBuilder<byte[]> content, PropertyBuilder<string> hash)
    {
        key.HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        content.HasMaxLength(GroupBrainContentProtector.MaximumEnvelopeLength); Hash(hash);
    }
    private static void PayloadChecks<T>(TableBuilder<T> table, string name) where T : class
    {
        table.HasCheckConstraint("CK_" + name + "_Values", "[Revision]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND DATEPART(tz,[CreatedAtUtc])=0");
        table.HasCheckConstraint("CK_" + name + "_Payload", "DATALENGTH([ProtectedContent]) BETWEEN 30 AND 64029 AND DATALENGTH([ContentKeyId]) BETWEEN 1 AND 64 AND [ContentKeyId] NOT LIKE '%[^0-9A-Za-z_-]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([EnvelopeSha256])=64 AND [EnvelopeSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
    }
}
