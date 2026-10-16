using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupModelAttemptResultModelConfiguration
{
    internal static void ConfigureGroupModelAttemptResults(this ModelBuilder model)
    {
        model.Entity<GroupAutomaticInputPlanRecord>().HasAlternateKey(x => new
        { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 });
        model.Entity<GroupModelAttemptStartRecord>().HasAlternateKey(x => new
        { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.RequestId, x.StartedAtUtc, x.DeadlineUtc });
        model.Entity<GroupModelAttemptResultRecord>(entity =>
        {
            entity.ToTable("GroupModelAttemptResults", table =>
            {
                table.HasCheckConstraint("CK_GroupModelAttemptResults_Format", "[FormatVersion]=1 AND DATALENGTH([ProtectedResult]) BETWEEN 229 AND 66029 AND SUBSTRING([ProtectedResult],1,1)=0x01");
                table.HasCheckConstraint("CK_GroupModelAttemptResults_Key", "DATALENGTH([EncryptionKeyId]) BETWEEN 1 AND 64 AND LEN([EncryptionKeyId])=DATALENGTH([EncryptionKeyId]) AND [EncryptionKeyId] NOT LIKE '%[^0-9A-Za-z_-]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint("CK_GroupModelAttemptResults_Identity",
                    "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [OperationId]<>'00000000-0000-0000-0000-000000000000' AND [ChunkOrdinal] BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_GroupModelAttemptResults_Hashes",
                    "DATALENGTH([PlanSha256])=64 AND [PlanSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([InputSha256])=64 AND [InputSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([ProtectedResultSha256])=64 AND [ProtectedResultSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint("CK_GroupModelAttemptResults_Time",
                    "DATEPART(tz,[StartedAtUtc])=0 AND DATEPART(tz,[DeadlineUtc])=0 AND DATEPART(tz,[ObservedAtUtc])=0 AND DATEPART(tz,[PersistedAtUtc])=0 AND [ObservedAtUtc]>=[StartedAtUtc] AND [ObservedAtUtc]<[DeadlineUtc] AND [PersistedAtUtc]>=[ObservedAtUtc] AND [PersistedAtUtc]<[DeadlineUtc]");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256, x.ChunkOrdinal }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId }).IsUnique();
            entity.Property(x => x.ProtectedResult).HasColumnType("varbinary(max)").HasMaxLength(GroupModelAttemptResultProtector.MaximumEnvelopeLength);
            entity.Property(x => x.EncryptionKeyId).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            foreach (var property in new[] { nameof(GroupModelAttemptResultRecord.PlanSha256), nameof(GroupModelAttemptResultRecord.InputSha256), nameof(GroupModelAttemptResultRecord.ProtectedResultSha256) })
                entity.Property<string>(property).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.HasOne<GroupAutomaticInputPlanRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupModelAttemptResults_OriginalPlan");
            entity.HasOne<GroupModelAttemptStartRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.RequestId, x.StartedAtUtc, x.DeadlineUtc })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.RequestId, x.StartedAtUtc, x.DeadlineUtc })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupModelAttemptResults_OriginalStart");
        });
    }
}
