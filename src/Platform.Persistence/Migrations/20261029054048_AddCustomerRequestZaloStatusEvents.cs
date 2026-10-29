using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerRequestZaloStatusEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomerRequestEvents_Values",
                schema: "aioffice",
                table: "CustomerRequestEvents");

            migrationBuilder.AlterColumn<string>(
                name: "ActorType",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(8)",
                oldMaxLength: 8,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "ActorExternalId",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "ActorName",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceMessageId",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRequestEvents_TenantId_CompanyId_RequestId_SourceMessageId",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                columns: new[] { "TenantId", "CompanyId", "RequestId", "SourceMessageId" },
                unique: true,
                filter: "[SourceMessageId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomerRequestEvents_Values",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                sql: "[EventType] IN ('created','status_changed','assignee_changed') AND [ActorType] IN ('service','user','zalo_member') AND DATEPART(tz,[Timestamp])=0 AND ([ActorType]<>'zalo_member' OR ([ActorName] IS NOT NULL AND [ActorExternalId] IS NOT NULL AND [SourceMessageId] IS NOT NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerRequestEvents_TenantId_CompanyId_RequestId_SourceMessageId",
                schema: "aioffice",
                table: "CustomerRequestEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomerRequestEvents_Values",
                schema: "aioffice",
                table: "CustomerRequestEvents");

            migrationBuilder.DropColumn(
                name: "ActorExternalId",
                schema: "aioffice",
                table: "CustomerRequestEvents");

            migrationBuilder.DropColumn(
                name: "ActorName",
                schema: "aioffice",
                table: "CustomerRequestEvents");

            migrationBuilder.DropColumn(
                name: "SourceMessageId",
                schema: "aioffice",
                table: "CustomerRequestEvents");

            migrationBuilder.AlterColumn<string>(
                name: "ActorType",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomerRequestEvents_Values",
                schema: "aioffice",
                table: "CustomerRequestEvents",
                sql: "[EventType] IN ('created','status_changed','assignee_changed') AND [ActorType] IN ('service','user') AND DATEPART(tz,[Timestamp])=0");
        }
    }
}
