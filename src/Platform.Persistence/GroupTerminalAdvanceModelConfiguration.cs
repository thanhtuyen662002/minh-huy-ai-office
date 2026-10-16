using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupTerminalAdvanceModelConfiguration
{
    internal static void ConfigureGroupTerminalAdvance(this ModelBuilder model)
    {
        model.Entity<GroupTerminalAdvanceRecord>(entity =>
        {
            entity.ToTable("GroupTerminalAdvances", table =>
            {
                table.HasCheckConstraint("CK_GroupTerminalAdvances_Envelope",
                    "[EnvelopeVersion]=1 AND DATALENGTH([Envelope]) BETWEEN 322 AND 385 AND SUBSTRING([Envelope],1,8)=0x41494F4746414431 AND DATALENGTH([Fingerprint])=32 AND HASHBYTES('SHA2_256',[Envelope])=[Fingerprint] AND DATALENGTH([TerminalManifestSha256])=32 AND DATALENGTH([AuthoritySha256])=32 AND [TerminalManifestSha256]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND [AuthoritySha256]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND DATALENGTH([KeyId]) BETWEEN 1 AND 64 AND DATALENGTH([KeyId])=LEN([KeyId]) AND [KeyId] NOT LIKE '%[^a-zA-Z0-9_-]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint("CK_GroupTerminalAdvances_Range",
                    "[Version]>0 AND [AfterSequence]>=0 AND [ThroughSequence]>[AfterSequence] AND [ThroughSequence]-[AfterSequence] BETWEEN 1 AND 500");
                // SQL composite FKs and CHECK UNKNOWN allow partial NULLs.
                // State every nullable predecessor member in BOTH alternatives.
                table.HasCheckConstraint("CK_GroupTerminalAdvances_Predecessor",
                    "([Version]=1 AND [AfterSequence]=0 AND [PreviousVersion] IS NULL AND [PreviousThroughSequence] IS NULL AND [PreviousFingerprint] IS NULL AND [PreviousAdvancedAtUtc] IS NULL) OR ([Version]>1 AND [AfterSequence]>0 AND [PreviousVersion] IS NOT NULL AND [PreviousThroughSequence] IS NOT NULL AND [PreviousFingerprint] IS NOT NULL AND [PreviousAdvancedAtUtc] IS NOT NULL AND [PreviousVersion]=[Version]-1 AND [PreviousThroughSequence]=[AfterSequence] AND DATALENGTH([PreviousFingerprint])=32 AND [PreviousFingerprint]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND DATEPART(tz,[PreviousAdvancedAtUtc])=0 AND [PreviousAdvancedAtUtc]<=[AdvancedAtUtc])");
                table.HasCheckConstraint("CK_GroupTerminalAdvances_Authority",
                    "[TerminalOperationId]<>'00000000-0000-0000-0000-000000000000' AND [ServiceId]<>'00000000-0000-0000-0000-000000000000' AND [CredentialEpoch]>0 AND [GrantVersion]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [AccountVersion]>0 AND DATEPART(tz,[TerminalCommittedAtUtc])=0 AND DATEPART(tz,[AdvancedAtUtc])=0 AND [TerminalCommittedAtUtc]<=[AdvancedAtUtc]");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.Version });
            entity.Property(x => x.Envelope).HasMaxLength(GroupTerminalAdvanceIntegrity.MaximumBytes).HasColumnType("varbinary(385)");
            foreach (var name in new[] { nameof(GroupTerminalAdvanceRecord.Fingerprint), nameof(GroupTerminalAdvanceRecord.PreviousFingerprint),
                nameof(GroupTerminalAdvanceRecord.TerminalManifestSha256), nameof(GroupTerminalAdvanceRecord.AuthoritySha256) })
                entity.Property<byte[]>(name).HasMaxLength(32).HasColumnType("varbinary(32)");
            entity.Property(x => x.KeyId).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupSourceStateRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupBatchTerminalReceiptRecord>().WithMany()
                .HasForeignKey(x => new
                {
                    x.TenantId,
                    x.CompanyId,
                    x.BindingId,
                    x.BatchId,
                    OperationId = x.TerminalOperationId,
                    x.AfterSequence,
                    x.ThroughSequence,
                    ManifestSha256 = x.TerminalManifestSha256,
                    CommittedAtUtc = x.TerminalCommittedAtUtc
                })
                .HasPrincipalKey(x => new
                {
                    x.TenantId,
                    x.CompanyId,
                    x.BindingId,
                    x.BatchId,
                    x.OperationId,
                    x.AfterSequence,
                    x.ThroughSequence,
                    x.ManifestSha256,
                    x.CommittedAtUtc
                }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupTerminalAdvanceRecord>().WithMany()
                .HasForeignKey(x => new
                {
                    x.TenantId,
                    x.CompanyId,
                    x.BindingId,
                    Version = x.PreviousVersion,
                    ThroughSequence = x.PreviousThroughSequence,
                    Fingerprint = x.PreviousFingerprint,
                    AdvancedAtUtc = x.PreviousAdvancedAtUtc
                })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.Version, x.ThroughSequence, x.Fingerprint, x.AdvancedAtUtc })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.AfterSequence }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId }).IsUnique();
        });
    }
}
