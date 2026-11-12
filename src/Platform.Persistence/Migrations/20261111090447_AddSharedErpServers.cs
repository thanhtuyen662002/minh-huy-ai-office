using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedErpServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErpServers",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(max)", maxLength: 8192, nullable: false),
                    Nonce = table.Column<byte[]>(type: "binary(12)", fixedLength: true, maxLength: 12, nullable: false),
                    Tag = table.Column<byte[]>(type: "binary(16)", fixedLength: true, maxLength: 16, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpServers", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ErpServers_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_ErpServers_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ErpServerAudits",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceCount = table.Column<int>(type: "int", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpServerAudits", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ErpServerAudits_ServerVersion", "[ServerVersion] > 0");
                    table.ForeignKey(
                        name: "FK_ErpServerAudits_ErpServers_TenantId_CompanyId_ServerId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServerId },
                        principalSchema: "aioffice",
                        principalTable: "ErpServers",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErpServerAudits_Users_TenantId_ActorUserId",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalSchema: "aioffice",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ErpServerSources",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Database = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpServerSources", x => new { x.TenantId, x.CompanyId, x.SourceId });
                    table.ForeignKey(
                        name: "FK_ErpServerSources_ErpServers_TenantId_CompanyId_ServerId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServerId },
                        principalSchema: "aioffice",
                        principalTable: "ErpServers",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErpServerSources_ProtectedDataSourceSecrets_TenantId_CompanyId_SourceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.SourceId },
                        principalSchema: "aioffice",
                        principalTable: "ProtectedDataSourceSecrets",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErpServerAudits_TenantId_ActorUserId",
                schema: "aioffice",
                table: "ErpServerAudits",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpServerAudits_TenantId_CompanyId_OperationId",
                schema: "aioffice",
                table: "ErpServerAudits",
                columns: new[] { "TenantId", "CompanyId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpServerAudits_TenantId_CompanyId_ServerId",
                schema: "aioffice",
                table: "ErpServerAudits",
                columns: new[] { "TenantId", "CompanyId", "ServerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpServers_TenantId_CompanyId_Name",
                schema: "aioffice",
                table: "ErpServers",
                columns: new[] { "TenantId", "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpServerSources_TenantId_CompanyId_ServerId",
                schema: "aioffice",
                table: "ErpServerSources",
                columns: new[] { "TenantId", "CompanyId", "ServerId" });
            // Same ownership and runtime rights as ProtectedDataSourceSecrets; the audit is append-only.
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[ErpServers] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[ErpServers] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[ErpServers] TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[ErpServerSources] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[ErpServerSources] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[ErpServerSources] TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[ErpServerAudits] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[ErpServerAudits] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[ErpServerAudits] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[ErpServerAudits]
                    ([TenantId], [CompanyId], [Id], [ActorUserId], [ServerId], [ServerVersion], [SourceCount], [OperationId], [RequestHash], [OccurredAtUtc])
                    TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Shared ERP server credentials and audit history require a reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
