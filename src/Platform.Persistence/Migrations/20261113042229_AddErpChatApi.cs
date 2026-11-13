using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddErpChatApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalUserId",
                schema: "aioffice",
                table: "Tasks",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ErpChatClients",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BillingAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(256)", maxLength: 256, nullable: false),
                    Nonce = table.Column<byte[]>(type: "binary(12)", fixedLength: true, maxLength: 12, nullable: false),
                    Tag = table.Column<byte[]>(type: "binary(16)", fixedLength: true, maxLength: 16, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpChatClients", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ErpChatClients_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_ErpChatClients_AiBillingAccounts_TenantId_CompanyId_BillingAccountId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BillingAccountId },
                        principalSchema: "aioffice",
                        principalTable: "AiBillingAccounts",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErpChatClients_CompanyMemberships_TenantId_CompanyId_ServiceUserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceUserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ErpChatNonces",
                schema: "aioffice",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nonce = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpChatNonces", x => new { x.ClientId, x.Nonce });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErpChatClients_Id",
                schema: "aioffice",
                table: "ErpChatClients",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpChatClients_TenantId_CompanyId_BillingAccountId",
                schema: "aioffice",
                table: "ErpChatClients",
                columns: new[] { "TenantId", "CompanyId", "BillingAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpChatClients_TenantId_CompanyId_Name",
                schema: "aioffice",
                table: "ErpChatClients",
                columns: new[] { "TenantId", "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpChatClients_TenantId_CompanyId_ServiceUserId",
                schema: "aioffice",
                table: "ErpChatClients",
                columns: new[] { "TenantId", "CompanyId", "ServiceUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpChatNonces_SeenAtUtc",
                schema: "aioffice",
                table: "ErpChatNonces",
                column: "SeenAtUtc");

            // Clients are never deleted by the runtime (turn them off instead); nonces are pruned as they age.
            migrationBuilder.Sql("DENY DELETE ON OBJECT::[aioffice].[ErpChatClients] TO [aioffice_binding_runtime];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("ERP chat clients and keys require a reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
