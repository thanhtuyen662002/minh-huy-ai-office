using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupBatchClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupBatchClaimReceipts",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    RequestedLifetimeTicks = table.Column<long>(type: "bigint", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    GrantVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AccountVersion = table.Column<long>(type: "bigint", nullable: false),
                    AuthoritySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBatchClaimReceipts", x => new { x.TenantId, x.CompanyId, x.BindingId, x.OperationId });
                    table.CheckConstraint("CK_GroupBatchClaimReceipts_Authority", "[CredentialEpoch] > 0 AND [GrantVersion] > 0 AND [SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [AccountVersion] > 0");
                    table.CheckConstraint("CK_GroupBatchClaimReceipts_Lease", "[Epoch] > 0 AND [RequestedLifetimeTicks] BETWEEN 100000000 AND 6000000000 AND [ExpiresAtUtc] > [IssuedAtUtc] AND DATEPART(tz,[IssuedAtUtc]) = 0 AND DATEPART(tz,[ExpiresAtUtc]) = 0");
                    table.ForeignKey(
                        name: "FK_GroupBatchClaimReceipts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchClaimReceipts_GroupBatchAllocations_TenantId_CompanyId_BindingId_BatchId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchAllocations",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchClaimReceipts_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupBatchClaimStates",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBatchClaimStates", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
                    table.CheckConstraint("CK_GroupBatchClaimStates_Lease", "[Epoch] > 0 AND [OwnerId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [ExpiresAtUtc] > [IssuedAtUtc] AND DATEPART(tz,[IssuedAtUtc]) = 0 AND DATEPART(tz,[ExpiresAtUtc]) = 0");
                    table.ForeignKey(
                        name: "FK_GroupBatchClaimStates_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchClaimStates_GroupBatchAllocations_TenantId_CompanyId_BindingId_BatchId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchAllocations",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchClaimReceipts_TenantId_CompanyId_BindingId_BatchId_Epoch",
                schema: "aioffice",
                table: "GroupBatchClaimReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "Epoch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchClaimReceipts_TenantId_CompanyId_ServiceId",
                schema: "aioffice",
                table: "GroupBatchClaimReceipts",
                columns: new[] { "TenantId", "CompanyId", "ServiceId" });
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupBatchClaimReceipts] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupBatchClaimReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupBatchClaimReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupBatchClaimReceipts] ([TenantId], [CompanyId], [BindingId], [OperationId], [BatchId], [OwnerId], [Epoch], [RequestedLifetimeTicks], [IssuedAtUtc], [ExpiresAtUtc], [ServiceId], [CredentialEpoch], [GrantVersion], [SourceVersion], [DeletionGeneration], [AccountVersion], [AuthoritySha256]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupBatchClaimStates] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupBatchClaimStates] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupBatchClaimStates] TO [aioffice_binding_runtime];
                GRANT UPDATE ON OBJECT::[aioffice].[GroupBatchClaimStates] ([Epoch], [OwnerId], [OperationId], [IssuedAtUtc], [ExpiresAtUtc]) TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupBatchClaimStates] ([TenantId], [CompanyId], [BindingId], [BatchId]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Committed group claims require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
