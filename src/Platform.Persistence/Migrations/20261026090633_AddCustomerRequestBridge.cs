using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerRequestBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerRequestCounters",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastNumber = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRequestCounters", x => new { x.TenantId, x.CompanyId });
                    table.CheckConstraint("CK_CustomerRequestCounters_Number", "[LastNumber]>0");
                    table.ForeignKey(
                        name: "FK_CustomerRequestCounters_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRequestNonces",
                schema: "aioffice",
                columns: table => new
                {
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nonce = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRequestNonces", x => new { x.ServiceId, x.Nonce });
                });

            migrationBuilder.CreateTable(
                name: "CustomerRequests",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourceGroupExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourceGroupName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReportedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CustomerDesiredDueText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Origin = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AssigneeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourceWindowFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceWindowTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRequests", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_CustomerRequests_Origin", "[Origin] = 'ai_extracted'");
                    table.CheckConstraint("CK_CustomerRequests_Status", "[Status] IN ('new','needs_clarification','in_progress','done','rejected')");
                    table.CheckConstraint("CK_CustomerRequests_Times", "[SourceWindowFrom] <= [SourceWindowTo] AND [CreatedAt] <= [UpdatedAt] AND DATEPART(tz,[SourceWindowFrom])=0 AND DATEPART(tz,[SourceWindowTo])=0 AND DATEPART(tz,[CreatedAt])=0 AND DATEPART(tz,[UpdatedAt])=0");
                    table.ForeignKey(
                        name: "FK_CustomerRequests_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRequests_CompanyMemberships_TenantId_CompanyId_AssigneeUserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.AssigneeUserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRequestEvents",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActorType = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRequestEvents", x => new { x.TenantId, x.CompanyId, x.RequestId, x.Id });
                    table.CheckConstraint("CK_CustomerRequestEvents_Values", "[EventType] IN ('created','status_changed','assignee_changed') AND [ActorType] IN ('service','user') AND DATEPART(tz,[Timestamp])=0");
                    table.ForeignKey(
                        name: "FK_CustomerRequestEvents_CustomerRequests_TenantId_CompanyId_RequestId",
                        columns: x => new { x.TenantId, x.CompanyId, x.RequestId },
                        principalSchema: "aioffice",
                        principalTable: "CustomerRequests",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRequestEvidence",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Quote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SourceMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SenderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MessageTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRequestEvidence", x => new { x.TenantId, x.CompanyId, x.RequestId, x.Ordinal });
                    table.CheckConstraint("CK_CustomerRequestEvidence_Time", "[Ordinal]>=0 AND DATEPART(tz,[MessageTimestamp])=0");
                    table.ForeignKey(
                        name: "FK_CustomerRequestEvidence_CustomerRequests_TenantId_CompanyId_RequestId",
                        columns: x => new { x.TenantId, x.CompanyId, x.RequestId },
                        principalSchema: "aioffice",
                        principalTable: "CustomerRequests",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRequestItems",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRequestItems", x => new { x.TenantId, x.CompanyId, x.RequestId, x.Ordinal });
                    table.CheckConstraint("CK_CustomerRequestItems_Values", "[Ordinal]>=0 AND [Priority] IN ('high','medium','low') AND [Kind] IN ('bug','feature','question','data')");
                    table.ForeignKey(
                        name: "FK_CustomerRequestItems_CustomerRequests_TenantId_CompanyId_RequestId",
                        columns: x => new { x.TenantId, x.CompanyId, x.RequestId },
                        principalSchema: "aioffice",
                        principalTable: "CustomerRequests",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRequestOpenQuestions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRequestOpenQuestions", x => new { x.TenantId, x.CompanyId, x.RequestId, x.Ordinal });
                    table.CheckConstraint("CK_CustomerRequestOpenQuestions_Ordinal", "[Ordinal]>=0");
                    table.ForeignKey(
                        name: "FK_CustomerRequestOpenQuestions_CustomerRequests_TenantId_CompanyId_RequestId",
                        columns: x => new { x.TenantId, x.CompanyId, x.RequestId },
                        principalSchema: "aioffice",
                        principalTable: "CustomerRequests",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequestEvents_TenantId_CompanyId_RequestId_Timestamp",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                columns: new[] { "TenantId", "CompanyId", "RequestId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequestNonces_ExpiresAt",
                schema: "aioffice",
                table: "CustomerRequestNonces",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_TenantId_CompanyId_AssigneeUserId",
                schema: "aioffice",
                table: "CustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "AssigneeUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_TenantId_CompanyId_Code",
                schema: "aioffice",
                table: "CustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_TenantId_CompanyId_CustomerName_CreatedAt",
                schema: "aioffice",
                table: "CustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "CustomerName", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_TenantId_CompanyId_IdempotencyKey",
                schema: "aioffice",
                table: "CustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequests_TenantId_CompanyId_Status_CreatedAt",
                schema: "aioffice",
                table: "CustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "Status", "CreatedAt" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[CustomerRequests] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[CustomerRequests] TO [aioffice_binding_runtime];
                GRANT UPDATE ON OBJECT::[aioffice].[CustomerRequests] ([Status], [AssigneeUserId], [UpdatedAt]) TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[CustomerRequests] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[CustomerRequests] ([TenantId], [CompanyId], [Id], [Code], [SourceGroupExternalId], [SourceGroupName], [CustomerName], [ReportedBy], [Summary], [CustomerDesiredDueText], [Origin], [IdempotencyKey], [SourceWindowFrom], [SourceWindowTo], [CreatedAt], [RowVersion]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[CustomerRequestCounters] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[CustomerRequestCounters] TO [aioffice_binding_runtime];
                GRANT UPDATE ON OBJECT::[aioffice].[CustomerRequestCounters] ([LastNumber]) TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[CustomerRequestCounters] TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[CustomerRequestNonces] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, DELETE ON OBJECT::[aioffice].[CustomerRequestNonces] TO [aioffice_binding_runtime];
                DENY UPDATE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[CustomerRequestNonces] TO [aioffice_binding_runtime];
                """);
            foreach (var (table, columns) in new[]
            {
                ("CustomerRequestItems", "[TenantId], [CompanyId], [RequestId], [Ordinal], [Content], [Priority], [Kind]"),
                ("CustomerRequestEvidence", "[TenantId], [CompanyId], [RequestId], [Ordinal], [Quote], [SourceMessageId], [SenderName], [MessageTimestamp]"),
                ("CustomerRequestOpenQuestions", "[TenantId], [CompanyId], [RequestId], [Ordinal], [Content]"),
                ("CustomerRequestEvents", "[TenantId], [CompanyId], [RequestId], [Id], [EventType], [ActorType], [ActorId], [OldValue], [NewValue], [Timestamp]")
            })
                migrationBuilder.Sql($"""
                    ALTER AUTHORIZATION ON OBJECT::[aioffice].[{table}] TO [aioffice_binding_operator_owner];
                    GRANT SELECT, INSERT ON OBJECT::[aioffice].[{table}] TO [aioffice_binding_runtime];
                    DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table}] TO [aioffice_binding_runtime];
                    DENY UPDATE ON OBJECT::[aioffice].[{table}] ({columns}) TO [aioffice_binding_runtime];
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerRequestCounters",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "CustomerRequestEvents",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "CustomerRequestEvidence",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "CustomerRequestItems",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "CustomerRequestNonces",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "CustomerRequestOpenQuestions",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "CustomerRequests",
                schema: "aioffice");
        }
    }
}
