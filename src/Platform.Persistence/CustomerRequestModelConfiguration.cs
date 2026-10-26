using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class CustomerRequestModelConfiguration
{
    private const string Binary = "Latin1_General_100_BIN2";

    internal static void ConfigureCustomerRequests(this ModelBuilder model)
    {
        model.Entity<CustomerRequestRecord>(entity =>
        {
            entity.ToTable("CustomerRequests", table =>
            {
                table.HasCheckConstraint("CK_CustomerRequests_Status", "[Status] IN ('new','needs_clarification','in_progress','done','rejected')");
                table.HasCheckConstraint("CK_CustomerRequests_Origin", "[Origin] = 'ai_extracted'");
                table.HasCheckConstraint("CK_CustomerRequests_Times", "[SourceWindowFrom] <= [SourceWindowTo] AND [CreatedAt] <= [UpdatedAt] AND DATEPART(tz,[SourceWindowFrom])=0 AND DATEPART(tz,[SourceWindowTo])=0 AND DATEPART(tz,[CreatedAt])=0 AND DATEPART(tz,[UpdatedAt])=0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(24).UseCollation(Binary);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200).UseCollation(Binary);
            entity.Property(x => x.SourceGroupExternalId).HasMaxLength(200).UseCollation(Binary);
            entity.Property(x => x.SourceGroupName).HasMaxLength(200);
            entity.Property(x => x.CustomerName).HasMaxLength(200);
            entity.Property(x => x.ReportedBy).HasMaxLength(200);
            entity.Property(x => x.Summary).HasMaxLength(8000);
            entity.Property(x => x.CustomerDesiredDueText).HasMaxLength(1000);
            entity.Property(x => x.Status).HasMaxLength(24).UseCollation(Binary);
            entity.Property(x => x.Origin).HasMaxLength(24).UseCollation(Binary);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status, x.CreatedAt });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.CustomerName, x.CreatedAt });
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, Id = x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, UserId = x.AssigneeUserId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CustomerRequestItemRecord>(entity =>
        {
            Child(entity, "CustomerRequestItems");
            entity.ToTable("CustomerRequestItems", table => table.HasCheckConstraint("CK_CustomerRequestItems_Values", "[Ordinal]>=0 AND [Priority] IN ('high','medium','low') AND [Kind] IN ('bug','feature','question','data')"));
            entity.Property(x => x.Content).HasMaxLength(4000);
            entity.Property(x => x.Priority).HasMaxLength(8).UseCollation(Binary);
            entity.Property(x => x.Kind).HasMaxLength(16).UseCollation(Binary);
        });
        model.Entity<CustomerRequestEvidenceRecord>(entity =>
        {
            Child(entity, "CustomerRequestEvidence");
            entity.ToTable("CustomerRequestEvidence", table => table.HasCheckConstraint("CK_CustomerRequestEvidence_Time", "[Ordinal]>=0 AND DATEPART(tz,[MessageTimestamp])=0"));
            entity.Property(x => x.Quote).HasMaxLength(2000);
            entity.Property(x => x.SourceMessageId).HasMaxLength(200).UseCollation(Binary);
            entity.Property(x => x.SenderName).HasMaxLength(200);
        });
        model.Entity<CustomerRequestOpenQuestionRecord>(entity =>
        {
            Child(entity, "CustomerRequestOpenQuestions");
            entity.ToTable("CustomerRequestOpenQuestions", table => table.HasCheckConstraint("CK_CustomerRequestOpenQuestions_Ordinal", "[Ordinal]>=0"));
            entity.Property(x => x.Content).HasMaxLength(1000);
        });
        model.Entity<CustomerRequestEventRecord>(entity =>
        {
            entity.ToTable("CustomerRequestEvents", table => table.HasCheckConstraint("CK_CustomerRequestEvents_Values", "[EventType] IN ('created','status_changed','assignee_changed') AND [ActorType] IN ('service','user') AND DATEPART(tz,[Timestamp])=0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.RequestId, x.Id });
            entity.Property(x => x.EventType).HasMaxLength(24).UseCollation(Binary);
            entity.Property(x => x.ActorType).HasMaxLength(8).UseCollation(Binary);
            entity.Property(x => x.OldValue).HasMaxLength(64);
            entity.Property(x => x.NewValue).HasMaxLength(64);
            entity.HasOne<CustomerRequestRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.RequestId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.RequestId, x.Timestamp });
        });
        model.Entity<CustomerRequestCounterRecord>(entity =>
        {
            entity.ToTable("CustomerRequestCounters", table => table.HasCheckConstraint("CK_CustomerRequestCounters_Number", "[LastNumber]>0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId });
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, Id = x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CustomerRequestNonceRecord>(entity =>
        {
            entity.ToTable("CustomerRequestNonces");
            entity.HasKey(x => new { x.ServiceId, x.Nonce });
            entity.HasIndex(x => x.ExpiresAt);
        });
    }

    private static void Child<T>(EntityTypeBuilder<T> entity, string table) where T : CustomerRequestChildRecord
    {
        entity.ToTable(table);
        entity.HasKey(x => new { x.TenantId, x.CompanyId, x.RequestId, x.Ordinal });
        entity.HasOne<CustomerRequestRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.RequestId }).OnDelete(DeleteBehavior.Restrict);
    }
}
