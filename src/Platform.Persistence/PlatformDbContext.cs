using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string DefaultSchema = "aioffice";

    public DbSet<PlatformMetadataRecord> PlatformMetadata => Set<PlatformMetadataRecord>();

    public DbSet<PlatformUserRecord> Users => Set<PlatformUserRecord>();

    public DbSet<CompanyRecord> Companies => Set<CompanyRecord>();

    public DbSet<CompanyMembershipRecord> CompanyMemberships => Set<CompanyMembershipRecord>();

    public DbSet<RoleAssignmentRecord> RoleAssignments => Set<RoleAssignmentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DefaultSchema);

        modelBuilder.Entity<PlatformMetadataRecord>(entity =>
        {
            entity.ToTable("PlatformMetadata");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(200);
            entity.Property(x => x.Value).HasMaxLength(4000);
            entity.Property(x => x.UpdatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
        });

        modelBuilder.Entity<PlatformUserRecord>(entity =>
        {
            entity.ToTable("Users");
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
            entity.ToTable("Companies");
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
            entity.ToTable("CompanyMemberships");
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
            entity.ToTable("RoleAssignments");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.UserId, x.RoleKey });
            entity.Property(x => x.RoleKey).HasMaxLength(100);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<CompanyMembershipRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.UserId })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
