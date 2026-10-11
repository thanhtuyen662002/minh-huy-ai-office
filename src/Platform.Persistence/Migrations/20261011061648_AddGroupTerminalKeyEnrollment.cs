using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupTerminalKeyEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No legacy mapping may be inferred from mutable present material.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [aioffice].[GroupTerminalAdvances]) THROW 51047, 'Existing signed advances require reviewed key enrollment migration.', 1;");
            migrationBuilder.CreateTable(
                name: "GroupTerminalAdvanceKeys",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FormatVersion = table.Column<int>(type: "int", nullable: false),
                    MaterialFingerprint = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    EnrolledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    GrantVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AccountVersion = table.Column<long>(type: "bigint", nullable: false),
                    AuthoritySha256 = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupTerminalAdvanceKeys", x => new { x.TenantId, x.CompanyId, x.BindingId, x.KeyId });
                    table.CheckConstraint("CK_GroupTerminalAdvanceKeys_Authority", "[ServiceId]<>'00000000-0000-0000-0000-000000000000' AND [CredentialEpoch]>0 AND [GrantVersion]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [AccountVersion]>0 AND DATEPART(tz,[EnrolledAtUtc])=0");
                    table.CheckConstraint("CK_GroupTerminalAdvanceKeys_Material", "[FormatVersion]=1 AND DATALENGTH([MaterialFingerprint])=32 AND [MaterialFingerprint]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND DATALENGTH([AuthoritySha256])=32 AND [AuthoritySha256]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND DATALENGTH([KeyId]) BETWEEN 1 AND 64 AND DATALENGTH([KeyId])=LEN([KeyId]) AND [KeyId] NOT LIKE '%[^a-zA-Z0-9_-]%' COLLATE Latin1_General_100_BIN2");
                    table.ForeignKey(
                        name: "FK_GroupTerminalAdvanceKeys_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalAdvanceKeys_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalAdvanceKeys_GroupSourceStates_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupSourceStates",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalAdvances_TenantId_CompanyId_BindingId_KeyId",
                schema: "aioffice",
                table: "GroupTerminalAdvances",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "KeyId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalAdvanceKeys_TenantId_CompanyId_ServiceId",
                schema: "aioffice",
                table: "GroupTerminalAdvanceKeys",
                columns: new[] { "TenantId", "CompanyId", "ServiceId" });

            migrationBuilder.AddForeignKey(
                name: "FK_GroupTerminalAdvances_GroupTerminalAdvanceKeys_TenantId_CompanyId_BindingId_KeyId",
                schema: "aioffice",
                table: "GroupTerminalAdvances",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "KeyId" },
                principalSchema: "aioffice",
                principalTable: "GroupTerminalAdvanceKeys",
                principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "KeyId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupTerminalAdvanceKeys] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupTerminalAdvanceKeys] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupTerminalAdvanceKeys] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupTerminalAdvanceKeys] ([TenantId], [CompanyId], [BindingId], [KeyId], [FormatVersion], [MaterialFingerprint], [EnrolledAtUtc], [ServiceId], [CredentialEpoch], [GrantVersion], [SourceVersion], [DeletionGeneration], [AccountVersion], [AuthoritySha256]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Terminal key enrollment requires reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
