using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupModelAttemptModelConfiguration
{
    internal static void ConfigureGroupModelAttempts(this ModelBuilder model)
    {
        // Expand an existing immutable receipt key so every journal start
        // binds its exact historical batch and epoch, not just the operation.
        model.Entity<GroupBatchClaimReceiptRecord>().HasAlternateKey(x => new
        { x.TenantId, x.CompanyId, x.BindingId, x.OperationId, x.BatchId, x.Epoch });
        model.Entity<GroupModelAttemptStartRecord>(entity =>
        {
            entity.ToTable("GroupModelAttemptStarts", table =>
            {
                table.HasCheckConstraint("CK_GroupModelAttemptStarts_Identity",
                    "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimEpoch]>0 AND DATALENGTH([InputSha256])=64 AND [InputSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint("CK_GroupModelAttemptStarts_Chain",
                    "[Ordinal] BETWEEN 1 AND 3 AND (([Ordinal]=1 AND [PredecessorOrdinal] IS NULL AND [PredecessorRequestId] IS NULL) OR ([Ordinal]>1 AND [PredecessorOrdinal] IS NOT NULL AND [PredecessorOrdinal]=[Ordinal]-1 AND [PredecessorRequestId] IS NOT NULL))");
                table.HasCheckConstraint("CK_GroupModelAttemptStarts_Time",
                    "DATEPART(tz,[StartedAtUtc])=0 AND DATEPART(tz,[DeadlineUtc])=0 AND [DeadlineUtc]>[StartedAtUtc] AND [DeadlineUtc]<=DATEADD(second,120,[StartedAtUtc])");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId });
            entity.Property(x => x.InputSha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.Ordinal }).IsUnique();
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.Ordinal, x.RequestId });
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.StartedAtUtc, x.DeadlineUtc });
            entity.HasOne<GroupBatchAllocationRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, Id = x.BatchId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchClaimReceiptRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, OperationId = x.ClaimOperationId, x.BatchId, Epoch = x.ClaimEpoch })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId, x.BatchId, x.Epoch })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupModelAttemptStarts_OriginalClaim");
            entity.HasOne<GroupModelAttemptStartRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, Ordinal = x.PredecessorOrdinal, RequestId = x.PredecessorRequestId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.Ordinal, x.RequestId })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupModelAttemptStarts_Predecessor");
        });
        model.Entity<GroupModelAttemptFailureRecord>(entity =>
        {
            entity.ToTable("GroupModelAttemptFailures", table =>
            {
                table.HasCheckConstraint("CK_GroupModelAttemptFailures_Outcome", "[Failure] BETWEEN 1 AND 4");
                table.HasCheckConstraint("CK_GroupModelAttemptFailures_Time",
                    "DATEPART(tz,[ObservedAtUtc])=0 AND [ObservedAtUtc]>=[StartedAtUtc] AND [ObservedAtUtc]<=[DeadlineUtc]");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId });
            entity.HasOne<GroupModelAttemptStartRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.StartedAtUtc, x.DeadlineUtc })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.StartedAtUtc, x.DeadlineUtc })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupModelAttemptFailures_OriginalStart");
        });
    }
}
