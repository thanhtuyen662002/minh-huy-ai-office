using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260919114500_InitialPlatformFoundation")]
public partial class InitialPlatformFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: PlatformDbContext.DefaultSchema);

        migrationBuilder.CreateTable(
            name: "PlatformMetadata",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                Key = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: false),
                Value = table.Column<string>(
                    type: "nvarchar(4000)",
                    maxLength: 4000,
                    nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlatformMetadata", x => x.Key);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PlatformMetadata",
            schema: PlatformDbContext.DefaultSchema);
    }
}
