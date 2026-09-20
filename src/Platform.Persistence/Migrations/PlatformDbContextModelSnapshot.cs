using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
public partial class PlatformDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasDefaultSchema(PlatformDbContext.DefaultSchema)
            .HasAnnotation("ProductVersion", "10.0.12")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);

        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

        modelBuilder.Entity<PlatformMetadataRecord>(entity =>
        {
            entity.ToTable("PlatformMetadata", PlatformDbContext.DefaultSchema);
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(200);
            entity.Property(x => x.Value).HasMaxLength(4000);
            entity.Property(x => x.UpdatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
        });

        modelBuilder.Entity<PlatformUserRecord>(entity =>
        {
            entity.ToTable("Users", PlatformDbContext.DefaultSchema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.IdentityProvider).HasMaxLength(100);
            entity.Property(x => x.Subject).HasMaxLength(200);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.HasIndex(x => new { x.TenantId, x.IdentityProvider, x.Subject })
                .IsUnique();
        });

        modelBuilder.Entity<CompanyRecord>(entity =>
        {
            entity.ToTable("Companies", PlatformDbContext.DefaultSchema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(100);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.HasIndex(x => new { x.TenantId, x.Code })
                .IsUnique();
        });

        modelBuilder.Entity<CompanyMembershipRecord>(entity =>
        {
            entity.ToTable("CompanyMemberships", PlatformDbContext.DefaultSchema);
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.UserId });
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<CompanyRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<PlatformUserRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.UserId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RoleAssignmentRecord>(entity =>
        {
            entity.ToTable("RoleAssignments", PlatformDbContext.DefaultSchema);
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.UserId, x.RoleKey });
            entity.Property(x => x.RoleKey).HasMaxLength(100);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<CompanyMembershipRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.UserId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<DataSourceRecord>(entity =>
        {
            entity.ToTable("DataSources", PlatformDbContext.DefaultSchema, table =>
            {
                table.HasCheckConstraint(
                    "CK_DataSources_ConnectionSecretReference",
                    "[ConnectionSecretReference] LIKE N'secretref://%'");
                table.HasCheckConstraint(
                    "CK_DataSources_AccessMode",
                    "[AllowRead] = CAST(1 AS bit) OR [AllowWrite] = CAST(1 AS bit)");
                table.HasCheckConstraint(
                    "CK_DataSources_MaxConcurrency",
                    "[MaxConcurrency] >= 1 AND [MaxConcurrency] <= 1024");
            });

            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.LogicalName).HasMaxLength(200);
            entity.Property(x => x.Kind).HasMaxLength(100);
            entity.Property(x => x.Environment).HasMaxLength(50);
            entity.Property(x => x.Purpose).HasMaxLength(200);
            entity.Property(x => x.ConnectionSecretReference)
                .HasMaxLength(DataSourceRecord.MaximumSecretReferenceLength);
            entity.Property(x => x.AllowRead).HasDefaultValue(true);
            entity.Property(x => x.AllowWrite).HasDefaultValue(false);
            entity.Property(x => x.MaxConcurrency).HasDefaultValue(1);
            entity.Property(x => x.IsEnabled).HasDefaultValue(true);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.Property(x => x.UpdatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.LogicalName })
                .IsUnique();

            entity.HasOne<CompanyRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .OnDelete(DeleteBehavior.Restrict);
        });
#pragma warning restore 612, 618
    }
}
