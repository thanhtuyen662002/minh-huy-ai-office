using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantImagesAndHiddenTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantImages",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantImages", x => new { x.TenantId, x.CompanyId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "HiddenTasks",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HiddenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiddenTasks", x => new { x.TenantId, x.CompanyId, x.TaskId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantImages_TenantId_CompanyId_UploadedByUserId_CreatedAtUtc",
                schema: "aioffice",
                table: "AssistantImages",
                columns: new[] { "TenantId", "CompanyId", "UploadedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HiddenTasks_TenantId_CompanyId_UserId",
                schema: "aioffice",
                table: "HiddenTasks",
                columns: new[] { "TenantId", "CompanyId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantImages",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "HiddenTasks",
                schema: "aioffice");
        }
    }
}
