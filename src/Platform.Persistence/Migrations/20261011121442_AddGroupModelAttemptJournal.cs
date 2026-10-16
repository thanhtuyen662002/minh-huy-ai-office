using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupModelAttemptJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_GroupBatchClaimReceipts_TenantId_CompanyId_BindingId_OperationId_BatchId_Epoch",
                schema: "aioffice",
                table: "GroupBatchClaimReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" });

            migrationBuilder.CreateTable(
                name: "GroupModelAttemptStarts",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InputSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    PredecessorOrdinal = table.Column<int>(type: "int", nullable: true),
                    PredecessorRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimEpoch = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupModelAttemptStarts", x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId });
                    table.UniqueConstraint("AK_GroupModelAttemptStarts_TenantId_CompanyId_BindingId_BatchId_InputSha256_Ordinal_RequestId", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.Ordinal, x.RequestId });
                    table.UniqueConstraint("AK_GroupModelAttemptStarts_TenantId_CompanyId_BindingId_RequestId_StartedAtUtc_DeadlineUtc", x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.StartedAtUtc, x.DeadlineUtc });
                    table.CheckConstraint("CK_GroupModelAttemptStarts_Chain", "[Ordinal] BETWEEN 1 AND 3 AND (([Ordinal]=1 AND [PredecessorOrdinal] IS NULL AND [PredecessorRequestId] IS NULL) OR ([Ordinal]>1 AND [PredecessorOrdinal] IS NOT NULL AND [PredecessorOrdinal]=[Ordinal]-1 AND [PredecessorRequestId] IS NOT NULL))");
                    table.CheckConstraint("CK_GroupModelAttemptStarts_Identity", "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimEpoch]>0 AND DATALENGTH([InputSha256])=64 AND [InputSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupModelAttemptStarts_Time", "DATEPART(tz,[StartedAtUtc])=0 AND DATEPART(tz,[DeadlineUtc])=0 AND [DeadlineUtc]>[StartedAtUtc] AND [DeadlineUtc]<=DATEADD(second,120,[StartedAtUtc])");
                    table.ForeignKey(
                        name: "FK_GroupModelAttemptStarts_GroupBatchAllocations_TenantId_CompanyId_BindingId_BatchId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchAllocations",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupModelAttemptStarts_OriginalClaim",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.ClaimOperationId, x.BatchId, x.ClaimEpoch },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchClaimReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupModelAttemptStarts_Predecessor",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.PredecessorOrdinal, x.PredecessorRequestId },
                        principalSchema: "aioffice",
                        principalTable: "GroupModelAttemptStarts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "Ordinal", "RequestId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupModelAttemptFailures",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Failure = table.Column<int>(type: "int", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupModelAttemptFailures", x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId });
                    table.CheckConstraint("CK_GroupModelAttemptFailures_Outcome", "[Failure] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_GroupModelAttemptFailures_Time", "DATEPART(tz,[ObservedAtUtc])=0 AND [ObservedAtUtc]>=[StartedAtUtc] AND [ObservedAtUtc]<=[DeadlineUtc]");
                    table.ForeignKey(
                        name: "FK_GroupModelAttemptFailures_OriginalStart",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.StartedAtUtc, x.DeadlineUtc },
                        principalSchema: "aioffice",
                        principalTable: "GroupModelAttemptStarts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "StartedAtUtc", "DeadlineUtc" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptFailures_TenantId_CompanyId_BindingId_RequestId_StartedAtUtc_DeadlineUtc",
                schema: "aioffice",
                table: "GroupModelAttemptFailures",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "StartedAtUtc", "DeadlineUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptStarts_TenantId_CompanyId_BindingId_BatchId_InputSha256_Ordinal",
                schema: "aioffice",
                table: "GroupModelAttemptStarts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptStarts_TenantId_CompanyId_BindingId_BatchId_InputSha256_PredecessorOrdinal_PredecessorRequestId",
                schema: "aioffice",
                table: "GroupModelAttemptStarts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "PredecessorOrdinal", "PredecessorRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptStarts_TenantId_CompanyId_BindingId_ClaimOperationId_BatchId_ClaimEpoch",
                schema: "aioffice",
                table: "GroupModelAttemptStarts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "ClaimOperationId", "BatchId", "ClaimEpoch" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupModelAttemptStarts] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupModelAttemptStarts] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupModelAttemptStarts] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupModelAttemptStarts] ([TenantId], [CompanyId], [BindingId], [RequestId], [BatchId], [InputSha256], [Ordinal], [PredecessorOrdinal], [PredecessorRequestId], [ClaimOperationId], [ClaimEpoch], [StartedAtUtc], [DeadlineUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupModelAttemptFailures] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupModelAttemptFailures] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupModelAttemptFailures] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupModelAttemptFailures] ([TenantId], [CompanyId], [BindingId], [RequestId], [Failure], [StartedAtUtc], [DeadlineUtc], [ObservedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Group model attempt history requires reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
