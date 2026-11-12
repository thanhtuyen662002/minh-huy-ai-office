using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageMetering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiBillingAccounts",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MonthlyBudget = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    QuestionsPerMinute = table.Column<int>(type: "int", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiBillingAccounts", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_AiBillingAccounts_Limits", "([MonthlyBudget] IS NULL OR [MonthlyBudget] >= 0) AND ([QuestionsPerMinute] IS NULL OR [QuestionsPerMinute] BETWEEN 1 AND 10000)");
                    table.CheckConstraint("CK_AiBillingAccounts_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_AiBillingAccounts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiPrices",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InputPerMillion = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CachedInputPerMillion = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    OutputPerMillion = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPrices", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_AiPrices_NonNegative", "[InputPerMillion] >= 0 AND [CachedInputPerMillion] >= 0 AND [OutputPerMillion] >= 0");
                    table.ForeignKey(
                        name: "FK_AiPrices_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiQuestionAdmissions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DataSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillingAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiQuestionAdmissions", x => new { x.TenantId, x.CompanyId, x.TaskId });
                    table.ForeignKey(
                        name: "FK_AiQuestionAdmissions_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiUsageRecords",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DataSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BillingAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CachedInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PriceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageRecords", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_AiUsageRecords_NonNegative", "[InputTokens] >= 0 AND [CachedInputTokens] >= 0 AND [CachedInputTokens] <= [InputTokens] AND [OutputTokens] >= 0 AND ([Cost] IS NULL OR [Cost] >= 0)");
                    table.ForeignKey(
                        name: "FK_AiUsageRecords_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiBillingAccountSources",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiBillingAccountSources", x => new { x.TenantId, x.CompanyId, x.SourceId });
                    table.ForeignKey(
                        name: "FK_AiBillingAccountSources_AiBillingAccounts_TenantId_CompanyId_AccountId",
                        columns: x => new { x.TenantId, x.CompanyId, x.AccountId },
                        principalSchema: "aioffice",
                        principalTable: "AiBillingAccounts",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiBillingAccountSources_DataSources_TenantId_CompanyId_SourceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.SourceId },
                        principalSchema: "aioffice",
                        principalTable: "DataSources",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiBillingAccounts_TenantId_CompanyId_Name",
                schema: "aioffice",
                table: "AiBillingAccounts",
                columns: new[] { "TenantId", "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiBillingAccountSources_TenantId_CompanyId_AccountId",
                schema: "aioffice",
                table: "AiBillingAccountSources",
                columns: new[] { "TenantId", "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiPrices_TenantId_CompanyId_Model_EffectiveFromUtc",
                schema: "aioffice",
                table: "AiPrices",
                columns: new[] { "TenantId", "CompanyId", "Model", "EffectiveFromUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiQuestionAdmissions_TenantId_CompanyId_AdmittedAtUtc",
                schema: "aioffice",
                table: "AiQuestionAdmissions",
                columns: new[] { "TenantId", "CompanyId", "AdmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiQuestionAdmissions_TenantId_CompanyId_BillingAccountId_AdmittedAtUtc",
                schema: "aioffice",
                table: "AiQuestionAdmissions",
                columns: new[] { "TenantId", "CompanyId", "BillingAccountId", "AdmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiQuestionAdmissions_TenantId_CompanyId_UserId_AdmittedAtUtc",
                schema: "aioffice",
                table: "AiQuestionAdmissions",
                columns: new[] { "TenantId", "CompanyId", "UserId", "AdmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageRecords_TenantId_CompanyId_BillingAccountId_OccurredAtUtc",
                schema: "aioffice",
                table: "AiUsageRecords",
                columns: new[] { "TenantId", "CompanyId", "BillingAccountId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageRecords_TenantId_CompanyId_OccurredAtUtc",
                schema: "aioffice",
                table: "AiUsageRecords",
                columns: new[] { "TenantId", "CompanyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageRecords_TenantId_CompanyId_TaskId",
                schema: "aioffice",
                table: "AiUsageRecords",
                columns: new[] { "TenantId", "CompanyId", "TaskId" });

            // Billing evidence is append-only for the runtime: usage, admissions and prices are inserted and read,
            // never changed or deleted (a price change is a new row). Customers and their source links stay editable.
            migrationBuilder.Sql("""
                DENY UPDATE, DELETE ON OBJECT::[aioffice].[AiUsageRecords] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE ON OBJECT::[aioffice].[AiQuestionAdmissions] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE ON OBJECT::[aioffice].[AiPrices] TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("AI usage and billing history require a reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
