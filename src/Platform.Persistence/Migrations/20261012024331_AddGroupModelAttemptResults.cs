using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupModelAttemptResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_GroupModelAttemptStarts_TenantId_CompanyId_BindingId_BatchId_InputSha256_RequestId_StartedAtUtc_DeadlineUtc",
                schema: "aioffice",
                table: "GroupModelAttemptStarts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "RequestId", "StartedAtUtc", "DeadlineUtc" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_GroupAutomaticInputPlans_TenantId_CompanyId_BindingId_BatchId_PlanSha256",
                schema: "aioffice",
                table: "GroupAutomaticInputPlans",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" });

            migrationBuilder.CreateTable(
                name: "GroupModelAttemptResults",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    InputSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ChunkOrdinal = table.Column<int>(type: "int", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FormatVersion = table.Column<int>(type: "int", nullable: false),
                    ProtectedResultSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    EncryptionKeyId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProtectedResult = table.Column<byte[]>(type: "varbinary(max)", maxLength: 66029, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PersistedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupModelAttemptResults", x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId });
                    table.CheckConstraint("CK_GroupModelAttemptResults_Format", "[FormatVersion]=1 AND DATALENGTH([ProtectedResult]) BETWEEN 229 AND 66029 AND SUBSTRING([ProtectedResult],1,1)=0x01");
                    table.CheckConstraint("CK_GroupModelAttemptResults_Hashes", "DATALENGTH([PlanSha256])=64 AND [PlanSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([InputSha256])=64 AND [InputSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([ProtectedResultSha256])=64 AND [ProtectedResultSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupModelAttemptResults_Identity", "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [OperationId]<>'00000000-0000-0000-0000-000000000000' AND [ChunkOrdinal] BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_GroupModelAttemptResults_Key", "DATALENGTH([EncryptionKeyId]) BETWEEN 1 AND 64 AND LEN([EncryptionKeyId])=DATALENGTH([EncryptionKeyId]) AND [EncryptionKeyId] NOT LIKE '%[^0-9A-Za-z_-]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupModelAttemptResults_Time", "DATEPART(tz,[StartedAtUtc])=0 AND DATEPART(tz,[DeadlineUtc])=0 AND DATEPART(tz,[ObservedAtUtc])=0 AND DATEPART(tz,[PersistedAtUtc])=0 AND [ObservedAtUtc]>=[StartedAtUtc] AND [ObservedAtUtc]<[DeadlineUtc] AND [PersistedAtUtc]>=[ObservedAtUtc] AND [PersistedAtUtc]<[DeadlineUtc]");
                    table.ForeignKey(
                        name: "FK_GroupModelAttemptResults_OriginalPlan",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 },
                        principalSchema: "aioffice",
                        principalTable: "GroupAutomaticInputPlans",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupModelAttemptResults_OriginalStart",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.InputSha256, x.RequestId, x.StartedAtUtc, x.DeadlineUtc },
                        principalSchema: "aioffice",
                        principalTable: "GroupModelAttemptStarts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "RequestId", "StartedAtUtc", "DeadlineUtc" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptResults_TenantId_CompanyId_BindingId_BatchId_InputSha256_RequestId_StartedAtUtc_DeadlineUtc",
                schema: "aioffice",
                table: "GroupModelAttemptResults",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "RequestId", "StartedAtUtc", "DeadlineUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptResults_TenantId_CompanyId_BindingId_BatchId_OperationId",
                schema: "aioffice",
                table: "GroupModelAttemptResults",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupModelAttemptResults_TenantId_CompanyId_BindingId_BatchId_PlanSha256_ChunkOrdinal",
                schema: "aioffice",
                table: "GroupModelAttemptResults",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256", "ChunkOrdinal" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupModelAttemptResults] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupModelAttemptResults] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupModelAttemptResults] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupModelAttemptResults] ([TenantId], [CompanyId], [BindingId], [RequestId], [BatchId], [PlanSha256], [InputSha256], [ChunkOrdinal], [OperationId], [FormatVersion], [ProtectedResultSha256], [EncryptionKeyId], [ProtectedResult], [StartedAtUtc], [DeadlineUtc], [ObservedAtUtc], [PersistedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Group model attempt results require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
