using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupBatchAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupBatchAllocations",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AfterSequence = table.Column<long>(type: "bigint", nullable: false),
                    AllocatedThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    ObservedCommittedThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    RawRevisionCount = table.Column<int>(type: "int", nullable: false),
                    IsHistoricalBackfill = table.Column<bool>(type: "bit", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AccountVersion = table.Column<long>(type: "bigint", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    GrantVersion = table.Column<long>(type: "bigint", nullable: false),
                    AllocatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBatchAllocations", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
                    table.CheckConstraint("CK_GroupBatchAllocations_Authority", "[SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [AccountVersion] > 0 AND [CredentialEpoch] > 0 AND [GrantVersion] > 0 AND DATEPART(tz,[AllocatedAtUtc]) = 0");
                    table.CheckConstraint("CK_GroupBatchAllocations_Range", "[AfterSequence] >= 0 AND [AllocatedThroughSequence] > [AfterSequence] AND [ObservedCommittedThroughSequence] >= [AllocatedThroughSequence] AND [RawRevisionCount] BETWEEN 1 AND 500 AND [AllocatedThroughSequence]-[AfterSequence]=[RawRevisionCount]");
                    table.ForeignKey(
                        name: "FK_GroupBatchAllocations_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchAllocations_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchAllocations_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupBatchAllocatedRevisions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommittedSequence = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsHistoricalBackfill = table.Column<bool>(type: "bit", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBatchAllocatedRevisions", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.CommittedSequence });
                    table.CheckConstraint("CK_GroupBatchAllocatedRevisions_Values", "[CommittedSequence] > 0 AND [Revision] > 0 AND [SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [Kind] IN (1,2,3,4) AND DATEPART(tz,[CommittedAtUtc]) = 0");
                    table.ForeignKey(
                        name: "FK_GroupBatchAllocatedRevisions_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchAllocatedRevisions_GroupBatchAllocations_TenantId_CompanyId_BindingId_BatchId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchAllocations",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBatchAllocatedRevisions_GroupMessageRevisions_TenantId_CompanyId_BindingId_MessageId_Revision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.Revision },
                        principalSchema: "aioffice",
                        principalTable: "GroupMessageRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchAllocatedRevisions_TenantId_CompanyId_BindingId_CommittedSequence",
                schema: "aioffice",
                table: "GroupBatchAllocatedRevisions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "CommittedSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchAllocatedRevisions_TenantId_CompanyId_BindingId_MessageId_Revision",
                schema: "aioffice",
                table: "GroupBatchAllocatedRevisions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchAllocations_TenantId_CompanyId_BindingId_AfterSequence",
                schema: "aioffice",
                table: "GroupBatchAllocations",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "AfterSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchAllocations_TenantId_CompanyId_BindingId_OperationId",
                schema: "aioffice",
                table: "GroupBatchAllocations",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBatchAllocations_TenantId_CompanyId_ServiceId",
                schema: "aioffice",
                table: "GroupBatchAllocations",
                columns: new[] { "TenantId", "CompanyId", "ServiceId" });

            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupBatchAllocations] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupBatchAllocations] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupBatchAllocations] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupBatchAllocations] ([TenantId], [CompanyId], [BindingId], [Id], [OperationId], [AfterSequence], [AllocatedThroughSequence], [ObservedCommittedThroughSequence], [RawRevisionCount], [IsHistoricalBackfill], [SourceVersion], [DeletionGeneration], [AccountVersion], [ServiceId], [CredentialEpoch], [GrantVersion], [AllocatedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupBatchAllocatedRevisions] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupBatchAllocatedRevisions] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupBatchAllocatedRevisions] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupBatchAllocatedRevisions] ([TenantId], [CompanyId], [BindingId], [BatchId], [CommittedSequence], [MessageId], [Revision], [ContentSha256], [Kind], [CommittedAtUtc], [IsHistoricalBackfill], [SourceVersion], [DeletionGeneration]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Committed group allocations require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
