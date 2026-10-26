using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                schema: "aioffice",
                table: "Users",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateTable(
                name: "CompanyInvitations",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RolesJson = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyInvitations", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_CompanyInvitations_Lifetime", "[ExpiresAtUtc] > [CreatedAtUtc] AND ([AcceptedAtUtc] IS NULL OR [RevokedAtUtc] IS NULL)");
                    table.CheckConstraint("CK_CompanyInvitations_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_CompanyInvitations_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyInvitations_Users_TenantId_InvitedByUserId",
                        columns: x => new { x.TenantId, x.InvitedByUserId },
                        principalSchema: "aioffice",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyInvitationAudits",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    InvitationVersion = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyInvitationAudits", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_CompanyInvitationAudits_CompanyInvitations_TenantId_CompanyId_InvitationId",
                        columns: x => new { x.TenantId, x.CompanyId, x.InvitationId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyInvitations",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyInvitationAudits_Users_TenantId_ActorUserId",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalSchema: "aioffice",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyInvitationAudits_TenantId_ActorUserId",
                schema: "aioffice",
                table: "CompanyInvitationAudits",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyInvitationAudits_TenantId_CompanyId_InvitationId",
                schema: "aioffice",
                table: "CompanyInvitationAudits",
                columns: new[] { "TenantId", "CompanyId", "InvitationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyInvitations_NormalizedEmail",
                schema: "aioffice",
                table: "CompanyInvitations",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyInvitations_TenantId_CompanyId_NormalizedEmail",
                schema: "aioffice",
                table: "CompanyInvitations",
                columns: new[] { "TenantId", "CompanyId", "NormalizedEmail" },
                unique: true,
                filter: "[AcceptedAtUtc] IS NULL AND [RevokedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyInvitations_TenantId_InvitedByUserId",
                schema: "aioffice",
                table: "CompanyInvitations",
                columns: new[] { "TenantId", "InvitedByUserId" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[CompanyInvitations] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[CompanyInvitations] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[CompanyInvitations] TO [aioffice_binding_runtime];

                ALTER AUTHORIZATION ON OBJECT::[aioffice].[CompanyInvitationAudits] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[CompanyInvitationAudits] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP
                    ON OBJECT::[aioffice].[CompanyInvitationAudits] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[CompanyInvitationAudits]
                    ([TenantId], [CompanyId], [Id], [InvitationId], [ActorUserId], [Action],
                     [InvitationVersion], [OccurredAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Company invitation history requires a reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
