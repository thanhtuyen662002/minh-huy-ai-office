using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupOriginalAdmissionSidecars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupOriginalAdmissionSidecars",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FormatVersion = table.Column<int>(type: "int", nullable: false),
                    PlanSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SidecarSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Sidecar = table.Column<byte[]>(type: "varbinary(max)", maxLength: 1010000, nullable: false),
                    OriginalClaimOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalClaimEpoch = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupOriginalAdmissionSidecars", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
                    table.CheckConstraint("CK_GroupOriginalAdmissionSidecars_Format", "[FormatVersion]=1 AND DATALENGTH([Sidecar]) BETWEEN 211 AND 1010000");
                    table.CheckConstraint("CK_GroupOriginalAdmissionSidecars_Identity", "[OriginalClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [OriginalClaimEpoch]>0 AND DATALENGTH([PlanSha256])=64 AND [PlanSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([SidecarSha256])=64 AND [SidecarSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupOriginalAdmissionSidecars_Time", "DATEPART(tz,[CreatedAtUtc])=0");
                    table.ForeignKey(
                        name: "FK_GroupOriginalAdmissionSidecars_OriginalClaim",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.OriginalClaimOperationId, x.BatchId, x.OriginalClaimEpoch },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchClaimReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupOriginalAdmissionSidecars_OriginalPlan",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 },
                        principalSchema: "aioffice",
                        principalTable: "GroupAutomaticInputPlans",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOriginalAdmissionSidecars_TenantId_CompanyId_BindingId_BatchId_PlanSha256",
                schema: "aioffice",
                table: "GroupOriginalAdmissionSidecars",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOriginalAdmissionSidecars_TenantId_CompanyId_BindingId_OriginalClaimOperationId_BatchId_OriginalClaimEpoch",
                schema: "aioffice",
                table: "GroupOriginalAdmissionSidecars",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OriginalClaimOperationId", "BatchId", "OriginalClaimEpoch" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupOriginalAdmissionSidecars] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupOriginalAdmissionSidecars] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupOriginalAdmissionSidecars] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupOriginalAdmissionSidecars] ([TenantId], [CompanyId], [BindingId], [BatchId], [FormatVersion], [PlanSha256], [SidecarSha256], [Sidecar], [OriginalClaimOperationId], [OriginalClaimEpoch], [CreatedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Group original admission provenance requires reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
