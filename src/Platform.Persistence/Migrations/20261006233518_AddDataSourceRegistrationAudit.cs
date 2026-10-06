using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceRegistrationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataSourceRegistrationAudits",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingVersion = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSourceRegistrationAudits", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_DataSourceRegistrationAudits_BindingVersion", "[BindingVersion] > 0");
                    table.ForeignKey(
                        name: "FK_DataSourceRegistrationAudits_DataSources_TenantId_CompanyId_DataSourceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.DataSourceId },
                        principalSchema: "aioffice",
                        principalTable: "DataSources",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DataSourceRegistrationAudits_Users_TenantId_ActorUserId",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalSchema: "aioffice",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceRegistrationAudits_TenantId_ActorUserId",
                schema: "aioffice",
                table: "DataSourceRegistrationAudits",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceRegistrationAudits_TenantId_CompanyId_DataSourceId",
                schema: "aioffice",
                table: "DataSourceRegistrationAudits",
                columns: new[] { "TenantId", "CompanyId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceRegistrationAudits_TenantId_CompanyId_OperationId",
                schema: "aioffice",
                table: "DataSourceRegistrationAudits",
                columns: new[] { "TenantId", "CompanyId", "OperationId" },
                unique: true);
            // Separate owner breaks dbo ownership chains. Runtime may append
            // audit rows, never mutate history or alter its authority boundary.
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[DataSourceRegistrationAudits] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[DataSourceRegistrationAudits] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP
                    ON OBJECT::[aioffice].[DataSourceRegistrationAudits] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[DataSourceRegistrationAudits]
                    ([TenantId], [CompanyId], [Id], [ActorUserId], [DataSourceId], [BindingId],
                     [BindingVersion], [OperationId], [RequestHash], [OccurredAtUtc])
                    TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Registration audit history requires a reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
