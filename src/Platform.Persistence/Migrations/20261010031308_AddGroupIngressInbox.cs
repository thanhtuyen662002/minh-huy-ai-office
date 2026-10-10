using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupIngressInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupIngressInbox",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CommittedSequence = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    GrantVersion = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupIngressInbox", x => new { x.TenantId, x.CompanyId, x.BindingId, x.EventId });
                    table.CheckConstraint("CK_GroupIngressInbox_Values", "[Revision] > 0 AND [CommittedSequence] > 0 AND [SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [CredentialEpoch] > 0 AND [GrantVersion] > 0 AND DATEPART(tz,[ReceivedAtUtc]) = 0");
                    table.ForeignKey(
                        name: "FK_GroupIngressInbox_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupIngressInbox_GroupIngressOutbox_TenantId_CompanyId_BindingId_EventId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.EventId },
                        principalSchema: "aioffice",
                        principalTable: "GroupIngressOutbox",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupIngressInbox_GroupMessageRevisions_TenantId_CompanyId_BindingId_MessageId_Revision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.Revision },
                        principalSchema: "aioffice",
                        principalTable: "GroupMessageRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupIngressInbox_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupIngressInbox_TenantId_CompanyId_BindingId_CommittedSequence",
                schema: "aioffice",
                table: "GroupIngressInbox",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "CommittedSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupIngressInbox_TenantId_CompanyId_BindingId_MessageId_Revision",
                schema: "aioffice",
                table: "GroupIngressInbox",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupIngressInbox_TenantId_CompanyId_ServiceId",
                schema: "aioffice",
                table: "GroupIngressInbox",
                columns: new[] { "TenantId", "CompanyId", "ServiceId" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupIngressInbox] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupIngressInbox] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupIngressInbox] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupIngressInbox] ([TenantId], [CompanyId], [BindingId], [EventId], [MessageId], [Revision], [CommittedSequence], [SourceVersion], [DeletionGeneration], [ServiceId], [CredentialEpoch], [GrantVersion], [ReceivedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Committed group worker receipts require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
