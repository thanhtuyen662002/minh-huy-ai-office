using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class BotChannelModelConfiguration
{
    internal static void ConfigureBotChannels(this ModelBuilder model)
    {
        model.Entity<BotChannelRecord>(entity =>
        {
            entity.ToTable("BotChannels", table =>
            {
                table.HasCheckConstraint("CK_BotChannels_Channel", "[Channel] IN ('zalo','whatsapp')");
                table.HasCheckConstraint("CK_BotChannels_Version", "[ConfigVersion]>0");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Channel });
            entity.Property(x => x.Channel).HasMaxLength(16).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.SummaryScheduleJson).HasMaxLength(15000);
            entity.Property(x => x.TechGroupExternalId).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.RowVersion).IsRowVersion();
            Company(entity);
        });
        model.Entity<BotCustomerGroupRecord>(entity =>
        {
            entity.ToTable("BotCustomerGroups");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Channel, x.ExternalId });
            entity.Property(x => x.Channel).HasMaxLength(16).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.ExternalId).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.CustomerName).HasMaxLength(200);
            entity.HasOne<BotChannelRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.Channel }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BotAiSettingsRecord>(entity =>
        {
            entity.ToTable("BotAiSettings", table => table.HasCheckConstraint("CK_BotAiSettings_Version", "[Version]>0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId });
            entity.Property(x => x.BaseUrl).HasMaxLength(1000);
            entity.Property(x => x.Model).HasMaxLength(200);
            entity.Property(x => x.ProtectedApiKey).HasMaxLength(8192);
            entity.Property(x => x.RowVersion).IsRowVersion();
            Company(entity);
        });
        model.Entity<BotStatusRecord>(entity =>
        {
            entity.ToTable("BotStatuses");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Channel });
            entity.Property(x => x.Channel).HasMaxLength(16).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.LoginState).HasMaxLength(32);
            entity.Property(x => x.GroupsJson).HasMaxLength(500000);
            entity.Property(x => x.LastError).HasMaxLength(500);
            entity.Property(x => x.LoginQrImage).HasMaxLength(65536);
            entity.Property(x => x.LoginQrContentType).HasMaxLength(32);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasOne<BotChannelRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.Channel }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BotCommandRecord>(entity =>
        {
            entity.ToTable("BotCommands", table => table.HasCheckConstraint("CK_BotCommands_Kind", "[Kind] IN ('summarize-now','relogin')"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Channel, x.Id });
            entity.Property(x => x.Channel).HasMaxLength(16).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.Kind).HasMaxLength(32);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Channel, x.ExpiresAt });
            entity.HasOne<BotChannelRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.Channel }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BotChannelAuditRecord>(entity =>
        {
            entity.ToTable("BotChannelAudits");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Channel).HasMaxLength(16);
            entity.Property(x => x.Field).HasMaxLength(64);
            entity.Property(x => x.Before).HasMaxLength(4000);
            entity.Property(x => x.After).HasMaxLength(4000);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Timestamp });
            Company(entity);
        });
    }

    private static void Company<T>(EntityTypeBuilder<T> entity) where T : class =>
        entity.HasOne<CompanyRecord>().WithMany().HasForeignKey("TenantId", "CompanyId").OnDelete(DeleteBehavior.Restrict);
}
