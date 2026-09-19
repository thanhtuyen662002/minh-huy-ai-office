using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260919151500_AddIdentityFoundation")]
public partial class AddIdentityFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Companies",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Companies", x => new { x.TenantId, x.Id });
            });

        migrationBuilder.CreateTable(
            name: "Users",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IdentityProvider = table.Column<string>(
                    type: "nvarchar(100)",
                    maxLength: 100,
                    nullable: false),
                Subject = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: false),
                DisplayName = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => new { x.TenantId, x.Id });
            });

        migrationBuilder.CreateTable(
            name: "CompanyMemberships",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_CompanyMemberships",
                    x => new { x.TenantId, x.CompanyId, x.UserId });
                table.ForeignKey(
                    name: "FK_CompanyMemberships_Companies_TenantId_CompanyId",
                    columns: x => new { x.TenantId, x.CompanyId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "Companies",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_CompanyMemberships_Users_TenantId_UserId",
                    columns: x => new { x.TenantId, x.UserId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "Users",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "RoleAssignments",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RoleKey = table.Column<string>(
                    type: "nvarchar(100)",
                    maxLength: 100,
                    nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_RoleAssignments",
                    x => new { x.TenantId, x.CompanyId, x.UserId, x.RoleKey });
                table.ForeignKey(
                    name: "FK_RoleAssignments_CompanyMemberships_TenantId_CompanyId_UserId",
                    columns: x => new { x.TenantId, x.CompanyId, x.UserId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "CompanyMemberships",
                    principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Companies_TenantId_Code",
            schema: PlatformDbContext.DefaultSchema,
            table: "Companies",
            columns: new[] { "TenantId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CompanyMemberships_TenantId_UserId",
            schema: PlatformDbContext.DefaultSchema,
            table: "CompanyMemberships",
            columns: new[] { "TenantId", "UserId" });

        migrationBuilder.CreateIndex(
            name: "IX_Users_TenantId_IdentityProvider_Subject",
            schema: PlatformDbContext.DefaultSchema,
            table: "Users",
            columns: new[] { "TenantId", "IdentityProvider", "Subject" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "RoleAssignments",
            schema: PlatformDbContext.DefaultSchema);

        migrationBuilder.DropTable(
            name: "CompanyMemberships",
            schema: PlatformDbContext.DefaultSchema);

        migrationBuilder.DropTable(
            name: "Companies",
            schema: PlatformDbContext.DefaultSchema);

        migrationBuilder.DropTable(
            name: "Users",
            schema: PlatformDbContext.DefaultSchema);
    }
}
