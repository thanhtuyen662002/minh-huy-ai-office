using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupListenerOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupAccountCoverageGaps",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ListenerEpoch = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAccountCoverageGaps", x => new { x.TenantId, x.CompanyId, x.ConnectorAccountId, x.ListenerEpoch, x.Reason });
                    table.CheckConstraint("CK_GroupAccountCoverageGaps_Values", "[ListenerEpoch] > 0 AND [Reason] IN ('listener-started','listener-expired','listener-stopped') AND [RecordedAtUtc] >= [OpenedAtUtc] AND DATEPART(tz,[OpenedAtUtc]) = 0 AND DATEPART(tz,[RecordedAtUtc]) = 0");
                    table.ForeignKey(
                        name: "FK_GroupAccountCoverageGaps_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupAccountCoverageGaps_GroupConnectorAccounts_TenantId_CompanyId_ConnectorAccountId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ConnectorAccountId },
                        principalSchema: "aioffice",
                        principalTable: "GroupConnectorAccounts",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupListenerCommandReceipts",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    Nonce = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ListenerEpoch = table.Column<long>(type: "bigint", nullable: false),
                    HeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Changed = table.Column<bool>(type: "bit", nullable: false),
                    CoverageRecorded = table.Column<bool>(type: "bit", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupListenerCommandReceipts", x => new { x.TenantId, x.CompanyId, x.ServiceId, x.CredentialEpoch, x.Nonce });
                    table.CheckConstraint("CK_GroupListenerCommandReceipts_Values", "[CredentialEpoch] > 0 AND [ListenerEpoch] > 0 AND [Operation] IN (1,2,3) AND [ExpiresAtUtc] >= [HeartbeatAtUtc] AND [CommittedAtUtc] >= [HeartbeatAtUtc] AND DATEPART(tz,[HeartbeatAtUtc]) = 0 AND DATEPART(tz,[ExpiresAtUtc]) = 0 AND DATEPART(tz,[CommittedAtUtc]) = 0");
                    table.ForeignKey(
                        name: "FK_GroupListenerCommandReceipts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupListenerCommandReceipts_GroupConnectorAccounts_TenantId_CompanyId_ConnectorAccountId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ConnectorAccountId },
                        principalSchema: "aioffice",
                        principalTable: "GroupConnectorAccounts",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupListenerCommandReceipts_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupListenerCommandReceipts_TenantId_CompanyId_ConnectorAccountId",
                schema: "aioffice",
                table: "GroupListenerCommandReceipts",
                columns: new[] { "TenantId", "CompanyId", "ConnectorAccountId" });

            // Frozen identifiers/permissions: evidence is append-only and its
            // ownership chain is independent from runtime-writable task rows.
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupAccountCoverageGaps] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupAccountCoverageGaps] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupAccountCoverageGaps] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupAccountCoverageGaps] ([TenantId], [CompanyId], [ConnectorAccountId], [ListenerEpoch], [Reason], [OpenedAtUtc], [RecordedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupListenerCommandReceipts] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupListenerCommandReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupListenerCommandReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupListenerCommandReceipts] ([TenantId], [CompanyId], [ServiceId], [CredentialEpoch], [Nonce], [ConnectorAccountId], [CommandSha256], [Operation], [OwnerId], [ListenerEpoch], [HeartbeatAtUtc], [ExpiresAtUtc], [Changed], [CoverageRecorded], [CommittedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Listener command receipts and interrupted coverage require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
