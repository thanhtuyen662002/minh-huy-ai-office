using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupModelAttemptAdmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_GroupOriginalAdmissionSidecars_Admission",
                schema: "aioffice",
                table: "GroupOriginalAdmissionSidecars",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256", "SidecarSha256" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_GroupModelAttemptStarts_Admission",
                schema: "aioffice",
                table: "GroupModelAttemptStarts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "BatchId", "InputSha256" });

            migrationBuilder.CreateTable(
                name: "GroupModelAttemptAdmissions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InputSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PlanSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SidecarSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ChunkOrdinal = table.Column<int>(type: "int", nullable: false),
                    ChunkOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FormatVersion = table.Column<int>(type: "int", nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Snapshot = table.Column<byte[]>(type: "varbinary(max)", maxLength: 10000, nullable: false),
                    PreparedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupModelAttemptAdmissions", x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId });
                    table.CheckConstraint("CK_GroupModelAttemptAdmissions_Chunk", "[ChunkOrdinal] BETWEEN 1 AND 100 AND [ChunkOperationId]<>'00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_GroupModelAttemptAdmissions_Format", "[FormatVersion]=1 AND DATALENGTH([Snapshot]) BETWEEN 256 AND 10000");
                    table.CheckConstraint("CK_GroupModelAttemptAdmissions_Identity", "DATALENGTH([InputSha256])=64 AND [InputSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([PlanSha256])=64 AND [PlanSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([SidecarSha256])=64 AND [SidecarSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([SnapshotSha256])=64 AND [SnapshotSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupModelAttemptAdmissions_Time", "DATEPART(tz,[PreparedAtUtc])=0 AND DATEPART(tz,[ExpiresAtUtc])=0 AND [ExpiresAtUtc]>[PreparedAtUtc] AND [ExpiresAtUtc]<=DATEADD(second,120,[PreparedAtUtc])");
                    table.ForeignKey(
                        name: "FK_GroupModelAttemptAdmissions_ExactOriginalAdmission",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256, x.SidecarSha256 },
                        principalSchema: "aioffice",
                        principalTable: "GroupOriginalAdmissionSidecars",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256", "SidecarSha256" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupModelAttemptAdmissions_ExactStart",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.BatchId, x.InputSha256 },
                        principalSchema: "aioffice",
                        principalTable: "GroupModelAttemptStarts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "BatchId", "InputSha256" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptAdmissions_TenantId_CompanyId_BindingId_BatchId_PlanSha256_SidecarSha256",
                schema: "aioffice",
                table: "GroupModelAttemptAdmissions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256", "SidecarSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptAdmissions_TenantId_CompanyId_BindingId_RequestId_BatchId_InputSha256",
                schema: "aioffice",
                table: "GroupModelAttemptAdmissions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "BatchId", "InputSha256" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupModelAttemptAdmissions] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupModelAttemptAdmissions] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupModelAttemptAdmissions] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupModelAttemptAdmissions] ([TenantId], [CompanyId], [BindingId], [RequestId], [BatchId], [InputSha256], [PlanSha256], [SidecarSha256], [ChunkOrdinal], [ChunkOperationId], [FormatVersion], [SnapshotSha256], [Snapshot], [PreparedAtUtc], [ExpiresAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Group model attempt admissions require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
