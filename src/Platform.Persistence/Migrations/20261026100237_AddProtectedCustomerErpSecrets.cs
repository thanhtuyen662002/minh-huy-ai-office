using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProtectedCustomerErpSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerErpProbeAudits",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerErpProbeAudits", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_CustomerErpProbeAudits_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProtectedDataSourceSecrets",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(max)", maxLength: 8192, nullable: false),
                    Nonce = table.Column<byte[]>(type: "binary(12)", fixedLength: true, maxLength: 12, nullable: false),
                    Tag = table.Column<byte[]>(type: "binary(16)", fixedLength: true, maxLength: 16, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectedDataSourceSecrets", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ProtectedDataSourceSecrets_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_ProtectedDataSourceSecrets_DataSources_TenantId_CompanyId_Id",
                        columns: x => new { x.TenantId, x.CompanyId, x.Id },
                        principalSchema: "aioffice",
                        principalTable: "DataSources",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProtectedDataSourceSecrets_Id",
                schema: "aioffice",
                table: "ProtectedDataSourceSecrets",
                column: "Id",
                unique: true);
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[ProtectedDataSourceSecrets] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[ProtectedDataSourceSecrets] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[ProtectedDataSourceSecrets] TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[CustomerErpProbeAudits] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[CustomerErpProbeAudits] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[CustomerErpProbeAudits] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[CustomerErpProbeAudits]
                    ([TenantId], [CompanyId], [Id], [ActorUserId], [Code], [OccurredAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Customer credentials and audit history require a reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
