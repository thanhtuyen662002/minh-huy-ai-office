using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupTerminalAdvanceKeyModelConfiguration
{
    internal static void ConfigureGroupTerminalAdvanceKeys(this ModelBuilder model)
    {
        model.Entity<GroupTerminalAdvanceKeyRecord>(entity =>
        {
            entity.ToTable("GroupTerminalAdvanceKeys", table =>
            {
                table.HasCheckConstraint("CK_GroupTerminalAdvanceKeys_Material",
                    "[FormatVersion]=1 AND DATALENGTH([MaterialFingerprint])=32 AND [MaterialFingerprint]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND DATALENGTH([AuthoritySha256])=32 AND [AuthoritySha256]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND DATALENGTH([KeyId]) BETWEEN 1 AND 64 AND DATALENGTH([KeyId])=LEN([KeyId]) AND [KeyId] NOT LIKE '%[^a-zA-Z0-9_-]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint("CK_GroupTerminalAdvanceKeys_Authority",
                    "[ServiceId]<>'00000000-0000-0000-0000-000000000000' AND [CredentialEpoch]>0 AND [GrantVersion]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [AccountVersion]>0 AND DATEPART(tz,[EnrolledAtUtc])=0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.KeyId });
            entity.Property(x => x.KeyId).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.MaterialFingerprint).HasMaxLength(32).HasColumnType("varbinary(32)");
            entity.Property(x => x.AuthoritySha256).HasMaxLength(32).HasColumnType("varbinary(32)");
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupSourceStateRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupTerminalAdvanceRecord>().HasOne<GroupTerminalAdvanceKeyRecord>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.KeyId }).OnDelete(DeleteBehavior.Restrict);
    }
}
