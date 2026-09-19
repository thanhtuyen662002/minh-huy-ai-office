using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string DefaultSchema = "aioffice";

    public DbSet<PlatformMetadataRecord> PlatformMetadata => Set<PlatformMetadataRecord>();

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
    }
}
