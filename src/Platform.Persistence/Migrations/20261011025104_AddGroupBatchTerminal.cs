using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupBatchTerminal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_GroupBatchClaimReceipts_TenantId_CompanyId_BindingId_BatchId_OperationId",
                schema: "aioffice",
                table: "GroupBatchClaimReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" });

            migrationBuilder.CreateTable(
                name: "GroupBatchTerminalReceipts",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ManifestVersion = table.Column<int>(type: "int", nullable: false),
                    Manifest = table.Column<byte[]>(type: "varbinary(max)", maxLength: 8177, nullable: false),
                    ManifestSha256 = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    AfterSequence = table.Column<long>(type: "bigint", nullable: false),
                    ThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    RawRevisionCount = table.Column<int>(type: "int", nullable: false),
                    SelectedMessageCount = table.Column<int>(type: "int", nullable: false),
                    ContributorCount = table.Column<int>(type: "int", nullable: false),
                    NoteCount = table.Column<int>(type: "int", nullable: false),
                    ClaimOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimOwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    GrantVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AccountVersion = table.Column<long>(type: "bigint", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBatchTerminalReceipts", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId });
                    table.CheckConstraint("CK_GroupBatchTerminalReceipts_Authority", "[OperationId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimOperationId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimOwnerId]<>'00000000-0000-0000-0000-000000000000' AND [ClaimEpoch]>0 AND [CredentialEpoch]>0 AND [GrantVersion]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [AccountVersion]>0 AND DATEPART(tz,[CommittedAtUtc])=0");
                    table.CheckConstraint("CK_GroupBatchTerminalReceipts_Manifest", "[ManifestVersion]=1 AND DATALENGTH([Manifest]) BETWEEN 257 AND 8177 AND SUBSTRING([Manifest],1,8)=0x41494F4754524D31 AND DATALENGTH([ManifestSha256])=32 AND HASHBYTES('SHA2_256',[Manifest])=[ManifestSha256]");
                    table.CheckConstraint("CK_GroupBatchTerminalReceipts_Range", "[AfterSequence]>=0 AND [ThroughSequence]>[AfterSequence] AND [ThroughSequence]-[AfterSequence]=[RawRevisionCount] AND [RawRevisionCount] BETWEEN 1 AND 500 AND [SelectedMessageCount] BETWEEN 1 AND 100 AND [SelectedMessageCount]<=[RawRevisionCount] AND [ContributorCount] BETWEEN 1 AND [SelectedMessageCount] AND [NoteCount] BETWEEN 0 AND [ContributorCount]*40");
                    table.ForeignKey(
                        name: "FK_GroupBatchTerminalReceipts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchTerminalReceipts_GroupBatchAllocations_TenantId_CompanyId_BindingId_BatchId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchAllocations",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchTerminalReceipts_GroupBatchClaimReceipts_TenantId_CompanyId_BindingId_BatchId_ClaimOperationId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.ClaimOperationId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchClaimReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchTerminalReceipts_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchTerminalReceipts_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupTerminalFrontierStates",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastTerminalBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupTerminalFrontierStates", x => new { x.TenantId, x.CompanyId, x.BindingId });
                    table.CheckConstraint("CK_GroupTerminalFrontierStates_Values", "[ThroughSequence]>=0 AND [Version]>0 AND (([ThroughSequence]=0 AND [LastTerminalBatchId] IS NULL) OR ([ThroughSequence]>0 AND [LastTerminalBatchId] IS NOT NULL)) AND DATEPART(tz,[UpdatedAtUtc])=0");
                    table.ForeignKey(
                        name: "FK_GroupTerminalFrontierStates_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalFrontierStates_GroupBatchTerminalReceipts_TenantId_CompanyId_BindingId_LastTerminalBatchId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.LastTerminalBatchId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchTerminalReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalFrontierStates_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTerminalFrontierStates_GroupSourceStates_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupSourceStates",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchTerminalReceipts_TenantId_CompanyId_BindingId_AfterSequence",
                schema: "aioffice",
                table: "GroupBatchTerminalReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "AfterSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchTerminalReceipts_TenantId_CompanyId_BindingId_BatchId_ClaimOperationId",
                schema: "aioffice",
                table: "GroupBatchTerminalReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "ClaimOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchTerminalReceipts_TenantId_CompanyId_BindingId_OperationId",
                schema: "aioffice",
                table: "GroupBatchTerminalReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchTerminalReceipts_TenantId_CompanyId_ServiceId",
                schema: "aioffice",
                table: "GroupBatchTerminalReceipts",
                columns: new[] { "TenantId", "CompanyId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalFrontierStates_TenantId_CompanyId_BindingId_LastTerminalBatchId",
                schema: "aioffice",
                table: "GroupTerminalFrontierStates",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "LastTerminalBatchId" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupBatchTerminalReceipts] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupBatchTerminalReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupBatchTerminalReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupBatchTerminalReceipts] ([TenantId], [CompanyId], [BindingId], [BatchId], [OperationId], [ManifestVersion], [Manifest], [ManifestSha256], [AfterSequence], [ThroughSequence], [RawRevisionCount], [SelectedMessageCount], [ContributorCount], [NoteCount], [ClaimOperationId], [ClaimOwnerId], [ClaimEpoch], [ServiceId], [CredentialEpoch], [GrantVersion], [SourceVersion], [DeletionGeneration], [AccountVersion], [CommittedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupTerminalFrontierStates] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[GroupTerminalFrontierStates] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupTerminalFrontierStates] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupTerminalFrontierStates] ([TenantId], [CompanyId], [BindingId]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Committed terminal receipts and contiguous progress require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
