using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupSignedTerminalAdvances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_GroupBatchTerminalReceipts_TenantId_CompanyId_BindingId_BatchId_OperationId_AfterSequence_ThroughSequence_ManifestSha256_Com~",
                schema: "aioffice",
                table: "GroupBatchTerminalReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId", "AfterSequence", "ThroughSequence", "ManifestSha256", "CommittedAtUtc" });

            migrationBuilder.CreateTable(
                name: "GroupTerminalAdvances",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AfterSequence = table.Column<long>(type: "bigint", nullable: false),
                    ThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    TerminalManifestSha256 = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    TerminalCommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PreviousVersion = table.Column<long>(type: "bigint", nullable: true),
                    PreviousThroughSequence = table.Column<long>(type: "bigint", nullable: true),
                    PreviousFingerprint = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: true),
                    PreviousAdvancedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EnvelopeVersion = table.Column<int>(type: "int", nullable: false),
                    Envelope = table.Column<byte[]>(type: "varbinary(385)", maxLength: 385, nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    KeyId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    GrantVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AccountVersion = table.Column<long>(type: "bigint", nullable: false),
                    AuthoritySha256 = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    AdvancedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupTerminalAdvances", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Version });
                    table.UniqueConstraint("AK_GroupTerminalAdvances_TenantId_CompanyId_BindingId_Version_ThroughSequence_Fingerprint_AdvancedAtUtc", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Version, x.ThroughSequence, x.Fingerprint, x.AdvancedAtUtc });
                    table.CheckConstraint("CK_GroupTerminalAdvances_Authority", "[TerminalOperationId]<>'00000000-0000-0000-0000-000000000000' AND [ServiceId]<>'00000000-0000-0000-0000-000000000000' AND [CredentialEpoch]>0 AND [GrantVersion]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [AccountVersion]>0 AND DATEPART(tz,[TerminalCommittedAtUtc])=0 AND DATEPART(tz,[AdvancedAtUtc])=0 AND [TerminalCommittedAtUtc]<=[AdvancedAtUtc]");
                    table.CheckConstraint("CK_GroupTerminalAdvances_Envelope", "[EnvelopeVersion]=1 AND DATALENGTH([Envelope]) BETWEEN 322 AND 385 AND SUBSTRING([Envelope],1,8)=0x41494F4746414431 AND DATALENGTH([Fingerprint])=32 AND HASHBYTES('SHA2_256',[Envelope])=[Fingerprint] AND DATALENGTH([TerminalManifestSha256])=32 AND DATALENGTH([AuthoritySha256])=32 AND [TerminalManifestSha256]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND [AuthoritySha256]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND DATALENGTH([KeyId]) BETWEEN 1 AND 64 AND DATALENGTH([KeyId])=LEN([KeyId]) AND [KeyId] NOT LIKE '%[^a-zA-Z0-9_-]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupTerminalAdvances_Predecessor", "([Version]=1 AND [AfterSequence]=0 AND [PreviousVersion] IS NULL AND [PreviousThroughSequence] IS NULL AND [PreviousFingerprint] IS NULL AND [PreviousAdvancedAtUtc] IS NULL) OR ([Version]>1 AND [AfterSequence]>0 AND [PreviousVersion] IS NOT NULL AND [PreviousThroughSequence] IS NOT NULL AND [PreviousFingerprint] IS NOT NULL AND [PreviousAdvancedAtUtc] IS NOT NULL AND [PreviousVersion]=[Version]-1 AND [PreviousThroughSequence]=[AfterSequence] AND DATALENGTH([PreviousFingerprint])=32 AND [PreviousFingerprint]<>0x0000000000000000000000000000000000000000000000000000000000000000 AND DATEPART(tz,[PreviousAdvancedAtUtc])=0 AND [PreviousAdvancedAtUtc]<=[AdvancedAtUtc])");
                    table.CheckConstraint("CK_GroupTerminalAdvances_Range", "[Version]>0 AND [AfterSequence]>=0 AND [ThroughSequence]>[AfterSequence] AND [ThroughSequence]-[AfterSequence] BETWEEN 1 AND 500");
                    table.ForeignKey(
                        name: "FK_GroupTerminalAdvances_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalAdvances_GroupBatchTerminalReceipts_TenantId_CompanyId_BindingId_BatchId_TerminalOperationId_AfterSequence_Thro~",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.TerminalOperationId, x.AfterSequence, x.ThroughSequence, x.TerminalManifestSha256, x.TerminalCommittedAtUtc },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchTerminalReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId", "AfterSequence", "ThroughSequence", "ManifestSha256", "CommittedAtUtc" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalAdvances_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalAdvances_GroupSourceStates_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupSourceStates",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalAdvances_GroupTerminalAdvances_TenantId_CompanyId_BindingId_PreviousVersion_PreviousThroughSequence_PreviousFin~",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.PreviousVersion, x.PreviousThroughSequence, x.PreviousFingerprint, x.PreviousAdvancedAtUtc },
                        principalSchema: "aioffice",
                        principalTable: "GroupTerminalAdvances",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence", "Fingerprint", "AdvancedAtUtc" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalAdvances_TenantId_CompanyId_BindingId_AfterSequence",
                schema: "aioffice",
                table: "GroupTerminalAdvances",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "AfterSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalAdvances_TenantId_CompanyId_BindingId_BatchId",
                schema: "aioffice",
                table: "GroupTerminalAdvances",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalAdvances_TenantId_CompanyId_BindingId_BatchId_TerminalOperationId_AfterSequence_ThroughSequence_TerminalManifes~",
                schema: "aioffice",
                table: "GroupTerminalAdvances",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "TerminalOperationId", "AfterSequence", "ThroughSequence", "TerminalManifestSha256", "TerminalCommittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalAdvances_TenantId_CompanyId_BindingId_PreviousVersion_PreviousThroughSequence_PreviousFingerprint_PreviousAdvan~",
                schema: "aioffice",
                table: "GroupTerminalAdvances",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "PreviousVersion", "PreviousThroughSequence", "PreviousFingerprint", "PreviousAdvancedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalAdvances_TenantId_CompanyId_ServiceId",
                schema: "aioffice",
                table: "GroupTerminalAdvances",
                columns: new[] { "TenantId", "CompanyId", "ServiceId" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupTerminalAdvances] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupTerminalAdvances] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupTerminalAdvances] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupTerminalAdvances] ([TenantId], [CompanyId], [BindingId], [Version], [BatchId], [TerminalOperationId], [AfterSequence], [ThroughSequence], [TerminalManifestSha256], [TerminalCommittedAtUtc], [PreviousVersion], [PreviousThroughSequence], [PreviousFingerprint], [PreviousAdvancedAtUtc], [EnvelopeVersion], [Envelope], [Fingerprint], [KeyId], [ServiceId], [CredentialEpoch], [GrantVersion], [SourceVersion], [DeletionGeneration], [AccountVersion], [AuthoritySha256], [AdvancedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Signed terminal history requires reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
