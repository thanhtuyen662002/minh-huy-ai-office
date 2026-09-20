using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260919153000_AddDataSourceRegistry")]
public partial class AddDataSourceRegistry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DataSources",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LogicalName = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: false),
                Kind = table.Column<string>(
                    type: "nvarchar(100)",
                    maxLength: 100,
                    nullable: false),
                Environment = table.Column<string>(
                    type: "nvarchar(50)",
                    maxLength: 50,
                    nullable: false),
                Purpose = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: false),
                ConnectionSecretReference = table.Column<string>(
                    type: "nvarchar(512)",
                    maxLength: 512,
                    nullable: false),
                AllowRead = table.Column<bool>(
                    type: "bit",
                    nullable: false,
                    defaultValue: true),
                AllowWrite = table.Column<bool>(
                    type: "bit",
                    nullable: false,
                    defaultValue: false),
                MaxConcurrency = table.Column<int>(
                    type: "int",
                    nullable: false,
                    defaultValue: 1),
                IsEnabled = table.Column<bool>(
                    type: "bit",
                    nullable: false,
                    defaultValue: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()"),
                UpdatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_DataSources",
                    x => new { x.TenantId, x.CompanyId, x.Id });
                table.ForeignKey(
                    name: "FK_DataSources_Companies_TenantId_CompanyId",
                    columns: x => new { x.TenantId, x.CompanyId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "Companies",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.CheckConstraint(
                    "CK_DataSources_ConnectionSecretReference",
                    "[ConnectionSecretReference] LIKE N'secretref://%'");
                table.CheckConstraint(
                    "CK_DataSources_AccessMode",
                    "[AllowRead] = CAST(1 AS bit) OR [AllowWrite] = CAST(1 AS bit)");
                table.CheckConstraint(
                    "CK_DataSources_MaxConcurrency",
                    "[MaxConcurrency] >= 1 AND [MaxConcurrency] <= 1024");
            });

        migrationBuilder.CreateIndex(
            name: "IX_DataSources_TenantId_CompanyId_LogicalName",
            schema: PlatformDbContext.DefaultSchema,
            table: "DataSources",
            columns: new[] { "TenantId", "CompanyId", "LogicalName" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DataSources",
            schema: PlatformDbContext.DefaultSchema);
    }
}
