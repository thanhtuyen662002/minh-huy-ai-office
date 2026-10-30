using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramTechLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramAccountLinks",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    TelegramName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramAccountLinks", x => new { x.TenantId, x.CompanyId, x.UserId });
                    table.CheckConstraint("CK_TelegramAccountLinks_Values", "[TelegramUserId] > 0 AND DATEPART(tz,[LinkedAt])=0");
                    table.ForeignKey(
                        name: "FK_TelegramAccountLinks_CompanyMemberships_TenantId_CompanyId_UserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.UserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TelegramLinkCodes",
                schema: "aioffice",
                columns: table => new
                {
                    CodeHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramLinkCodes", x => x.CodeHash);
                    table.CheckConstraint("CK_TelegramLinkCodes_Hash", "DATALENGTH([CodeHash]) = 32");
                    table.ForeignKey(
                        name: "FK_TelegramLinkCodes_CompanyMemberships_TenantId_CompanyId_UserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.UserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAccountLinks_TenantId_CompanyId_TelegramUserId",
                schema: "aioffice",
                table: "TelegramAccountLinks",
                columns: new[] { "TenantId", "CompanyId", "TelegramUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkCodes_ExpiresAt",
                schema: "aioffice",
                table: "TelegramLinkCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkCodes_TenantId_CompanyId_UserId",
                schema: "aioffice",
                table: "TelegramLinkCodes",
                columns: new[] { "TenantId", "CompanyId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramAccountLinks",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "TelegramLinkCodes",
                schema: "aioffice");
        }
    }
}
