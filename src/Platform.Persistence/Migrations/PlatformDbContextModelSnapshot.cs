using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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

        modelBuilder.Entity(
            "MinhHuy.AIOffice.Platform.Persistence.PlatformMetadataRecord",
            b =>
            {
                b.Property<string>("Key")
                    .HasMaxLength(200)
                    .HasColumnType("nvarchar(200)");

                b.Property<DateTimeOffset>("UpdatedAtUtc")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("datetimeoffset")
                    .HasDefaultValueSql("SYSDATETIMEOFFSET()");

                b.Property<string>("Value")
                    .HasMaxLength(4000)
                    .HasColumnType("nvarchar(4000)");

                b.HasKey("Key");

                b.ToTable("PlatformMetadata", PlatformDbContext.DefaultSchema);
            });
#pragma warning restore 612, 618
    }
}
