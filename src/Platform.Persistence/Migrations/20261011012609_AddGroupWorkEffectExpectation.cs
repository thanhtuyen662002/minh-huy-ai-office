using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupWorkEffectExpectation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EffectLedgerVersion",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "ExpectedEffectSha256",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                type: "varbinary(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupWorkCommitReceipts_Effects",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                sql: "([EffectLedgerVersion]=0 AND [ExpectedEffectSha256] IS NULL) OR ([EffectLedgerVersion]=1 AND [DependencyManifestVersion]=1 AND [ExpectedEffectSha256] IS NOT NULL AND DATALENGTH([ExpectedEffectSha256])=32)");
            migrationBuilder.Sql("""
                DENY UPDATE ON OBJECT::[aioffice].[GroupWorkCommitReceipts] ([EffectLedgerVersion], [ExpectedEffectSha256]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Committed original effect expectations require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
