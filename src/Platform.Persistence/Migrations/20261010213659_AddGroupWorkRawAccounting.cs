using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupWorkRawAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupWorkRawDispositions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommittedSequence = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RawRevision = table.Column<long>(type: "bigint", nullable: false),
                    SelectedMessageRevision = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Relation = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupWorkRawDispositions", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.CommittedSequence });
                    table.CheckConstraint("CK_GroupWorkRawDispositions_Values", "[CommittedSequence]>0 AND [RawRevision]>0 AND [SelectedMessageRevision]>0 AND [Outcome] IN (1,2,3,4,5,6,7,8) AND (([Relation]=1 AND [RawRevision]=[SelectedMessageRevision]) OR ([Relation]=2 AND [RawRevision]<>[SelectedMessageRevision]))");
                    table.ForeignKey(
                        name: "FK_GroupWorkRawDispositions_GroupBatchAllocatedRevisions_TenantId_CompanyId_BindingId_BatchId_CommittedSequence",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.CommittedSequence },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchAllocatedRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "CommittedSequence" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupWorkRawDispositions_GroupMessageRevisions_TenantId_CompanyId_BindingId_MessageId_RawRevision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.RawRevision },
                        principalSchema: "aioffice",
                        principalTable: "GroupMessageRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupWorkRawDispositions_GroupWorkCommitReceipts_TenantId_CompanyId_BindingId_BatchId_OperationId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId },
                        principalSchema: "aioffice",
                        principalTable: "GroupWorkCommitReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupWorkRawDispositions_GroupWorkSourceDispositions_TenantId_CompanyId_BindingId_BatchId_MessageId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.MessageId },
                        principalSchema: "aioffice",
                        principalTable: "GroupWorkSourceDispositions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "MessageId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupWorkRawDispositions_TenantId_CompanyId_BindingId_BatchId_MessageId",
                schema: "aioffice",
                table: "GroupWorkRawDispositions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupWorkRawDispositions_TenantId_CompanyId_BindingId_BatchId_OperationId",
                schema: "aioffice",
                table: "GroupWorkRawDispositions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupWorkRawDispositions_TenantId_CompanyId_BindingId_MessageId_RawRevision",
                schema: "aioffice",
                table: "GroupWorkRawDispositions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "RawRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupWorkRawDispositions_TenantId_CompanyId_BindingId_OperationId_CommittedSequence",
                schema: "aioffice",
                table: "GroupWorkRawDispositions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "CommittedSequence" });
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupWorkRawDispositions] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupWorkRawDispositions] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupWorkRawDispositions] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupWorkRawDispositions] ([TenantId], [CompanyId], [BindingId], [BatchId], [CommittedSequence], [MessageId], [RawRevision], [SelectedMessageRevision], [OperationId], [Outcome], [Relation]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Committed raw source accounting requires reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
