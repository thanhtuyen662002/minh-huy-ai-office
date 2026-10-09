using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerTaskHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Tasks_OwnerHistory",
                schema: "aioffice",
                table: "Tasks",
                columns: new[] { "TenantId", "CompanyId", "CreatedByUserId", "CreatedAtUtc", "Id" },
                descending: new[] { false, false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_OwnerHistory",
                schema: "aioffice",
                table: "Tasks");
        }
    }
}
