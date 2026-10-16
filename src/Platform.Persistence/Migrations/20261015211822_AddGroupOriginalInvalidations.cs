using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupOriginalInvalidations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupOriginalInvalidations",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FormatVersion = table.Column<int>(type: "int", nullable: false),
                    PlanSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OriginalClaimOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalClaimEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ClaimOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ObservedDependenciesSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CheckpointSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupOriginalInvalidations", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
                    table.CheckConstraint("CK_GroupOriginalInvalidations_Claims", "[OriginalClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [OriginalClaimEpoch]>0 AND [ClaimEpoch]>=[OriginalClaimEpoch] AND ([ClaimEpoch]>[OriginalClaimEpoch] OR [ClaimOperationId]=[OriginalClaimOperationId])");
                    table.CheckConstraint("CK_GroupOriginalInvalidations_Format", "[FormatVersion]=1");
                    table.CheckConstraint("CK_GroupOriginalInvalidations_Hashes", "DATALENGTH([PlanSha256])=64 AND [PlanSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([ObservedDependenciesSha256])=64 AND [ObservedDependenciesSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([CheckpointSha256])=64 AND [CheckpointSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupOriginalInvalidations_Time", "DATEPART(tz,[CreatedAtUtc])=0");
                    table.ForeignKey(
                        name: "FK_GroupOriginalInvalidations_CheckpointClaim",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.ClaimOperationId, x.BatchId, x.ClaimEpoch },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchClaimReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupOriginalInvalidations_OriginalClaim",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.OriginalClaimOperationId, x.BatchId, x.OriginalClaimEpoch },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchClaimReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupOriginalInvalidations_OriginalPlan",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 },
                        principalSchema: "aioffice",
                        principalTable: "GroupAutomaticInputPlans",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOriginalInvalidations_TenantId_CompanyId_BindingId_BatchId_PlanSha256",
                schema: "aioffice",
                table: "GroupOriginalInvalidations",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOriginalInvalidations_TenantId_CompanyId_BindingId_ClaimOperationId_BatchId_ClaimEpoch",
                schema: "aioffice",
                table: "GroupOriginalInvalidations",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "ClaimOperationId", "BatchId", "ClaimEpoch" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOriginalInvalidations_TenantId_CompanyId_BindingId_OriginalClaimOperationId_BatchId_OriginalClaimEpoch",
                schema: "aioffice",
                table: "GroupOriginalInvalidations",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OriginalClaimOperationId", "BatchId", "OriginalClaimEpoch" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupOriginalInvalidations] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupOriginalInvalidations] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupOriginalInvalidations] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupOriginalInvalidations] ([TenantId], [CompanyId], [BindingId], [BatchId], [FormatVersion], [PlanSha256], [OriginalClaimOperationId], [OriginalClaimEpoch], [ClaimOperationId], [ClaimEpoch], [ObservedDependenciesSha256], [CheckpointSha256], [CreatedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Group original invalidations require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
