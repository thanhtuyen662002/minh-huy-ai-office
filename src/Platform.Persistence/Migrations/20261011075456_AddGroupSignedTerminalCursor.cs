using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupSignedTerminalCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve exact existing rows. Refuse unsigned/empty legacy
            // cursors before DDL; migration cannot derive a trusted MAC.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [aioffice].[GroupTerminalFrontierStates] c WITH (UPDLOCK,HOLDLOCK)
                    WHERE c.[ThroughSequence]<=0 OR c.[Version]<=0 OR c.[LastTerminalBatchId] IS NULL
                        OR DATEPART(tz,c.[UpdatedAtUtc])<>0 OR NOT EXISTS (
                            SELECT 1 FROM [aioffice].[GroupTerminalAdvances] a WITH (UPDLOCK,HOLDLOCK)
                            WHERE a.[TenantId]=c.[TenantId] AND a.[CompanyId]=c.[CompanyId]
                                AND a.[BindingId]=c.[BindingId] AND a.[Version]=c.[Version]
                                AND a.[ThroughSequence]=c.[ThroughSequence]
                                AND a.[BatchId]=c.[LastTerminalBatchId]
                                AND a.[AdvancedAtUtc]=c.[UpdatedAtUtc]))
                    THROW 51048, 'Existing terminal cursor requires reviewed signed history migration.', 1;
                """);
            migrationBuilder.AddUniqueConstraint(
                name: "AK_GroupTerminalAdvances_TenantId_CompanyId_BindingId_Version_ThroughSequence_BatchId_AdvancedAtUtc",
                schema: "aioffice",
                table: "GroupTerminalAdvances",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence", "BatchId", "AdvancedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTerminalFrontierStates_TenantId_CompanyId_BindingId_Version_ThroughSequence_LastTerminalBatchId_UpdatedAtUtc",
                schema: "aioffice",
                table: "GroupTerminalFrontierStates",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence", "LastTerminalBatchId", "UpdatedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupTerminalFrontierStates_Signed",
                schema: "aioffice",
                table: "GroupTerminalFrontierStates",
                sql: "[ThroughSequence]>0 AND [Version]>0 AND [LastTerminalBatchId] IS NOT NULL AND DATEPART(tz,[UpdatedAtUtc])=0");

            migrationBuilder.AddForeignKey(
                name: "FK_GroupTerminalFrontierStates_SignedAdvance",
                schema: "aioffice",
                table: "GroupTerminalFrontierStates",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence", "LastTerminalBatchId", "UpdatedAtUtc" },
                principalSchema: "aioffice",
                principalTable: "GroupTerminalAdvances",
                principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence", "BatchId", "AdvancedAtUtc" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Signed terminal cursor constraints cannot be removed by automatic rollback.");
        }
    }
}
