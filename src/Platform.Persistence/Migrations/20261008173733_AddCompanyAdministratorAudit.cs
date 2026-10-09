using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyAdministratorAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyAdministratorAudits",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    BeforeAdministrator = table.Column<bool>(type: "bit", nullable: false),
                    AfterAdministrator = table.Column<bool>(type: "bit", nullable: false),
                    BeforeRolesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AfterRolesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BeforeVersion = table.Column<long>(type: "bigint", nullable: false),
                    AfterVersion = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyAdministratorAudits", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_CompanyAdministratorAudits_Versions", "[BeforeVersion] > 0 AND [AfterVersion] >= [BeforeVersion]");
                    table.ForeignKey(
                        name: "FK_CompanyAdministratorAudits_CompanyMemberships_TenantId_CompanyId_TargetUserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.TargetUserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyAdministratorAudits_Users_TenantId_ActorUserId",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalSchema: "aioffice",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAdministratorAudits_TenantId_ActorUserId",
                schema: "aioffice",
                table: "CompanyAdministratorAudits",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAdministratorAudits_TenantId_CompanyId_OperationId",
                schema: "aioffice",
                table: "CompanyAdministratorAudits",
                columns: new[] { "TenantId", "CompanyId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAdministratorAudits_TenantId_CompanyId_TargetUserId",
                schema: "aioffice",
                table: "CompanyAdministratorAudits",
                columns: new[] { "TenantId", "CompanyId", "TargetUserId" });
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[CompanyAdministratorAudits] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[CompanyAdministratorAudits] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP
                    ON OBJECT::[aioffice].[CompanyAdministratorAudits] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[CompanyAdministratorAudits]
                    ([TenantId], [CompanyId], [Id], [ActorUserId], [TargetUserId], [OperationId], [RequestHash],
                     [BeforeAdministrator], [AfterAdministrator], [BeforeRolesJson], [AfterRolesJson],
                     [BeforeVersion], [AfterVersion], [OccurredAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Administrator audit history requires a reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
