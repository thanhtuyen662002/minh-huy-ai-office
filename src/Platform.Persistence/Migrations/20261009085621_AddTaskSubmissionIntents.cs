using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskSubmissionIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskSubmissionIntents",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InputVersion = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    Question = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false, collation: "Latin1_General_100_BIN2"),
                    InputFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSubmissionIntents", x => new { x.TenantId, x.CompanyId, x.UserId, x.OperationId });
                    table.CheckConstraint("CK_TaskSubmissionIntents_Input", "[InputVersion] = 1 AND [MaxAttempts] = 3");
                    table.CheckConstraint("CK_TaskSubmissionIntents_Lifetime", "[ExpiresAtUtc] = DATEADD(hour,24,[CreatedAtUtc])");
                    table.CheckConstraint("CK_TaskSubmissionIntents_Question", "DATALENGTH([Question]) BETWEEN 2 AND 8000");
                    table.ForeignKey(
                        name: "FK_TaskSubmissionIntents_CompanyMemberships_TenantId_CompanyId_UserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.UserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskSubmissionIntents_TenantId_CompanyId_UserId_CreatedAtUtc_OperationId",
                schema: "aioffice",
                table: "TaskSubmissionIntents",
                columns: new[] { "TenantId", "CompanyId", "UserId", "CreatedAtUtc", "OperationId" },
                descending: new[] { false, false, false, true, true });
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[TaskSubmissionIntents] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[TaskSubmissionIntents] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP
                    ON OBJECT::[aioffice].[TaskSubmissionIntents] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[TaskSubmissionIntents]
                    ([TenantId], [CompanyId], [UserId], [OperationId], [DataSourceId], [InputVersion], [MaxAttempts],
                     [Question], [InputFingerprint], [CreatedAtUtc], [ExpiresAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Durable submission intents require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
