using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupWorkDependencyManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "DependencyManifest",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                type: "varbinary(6902)",
                maxLength: 6902,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DependencyManifestVersion",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupWorkCommitReceipts_Dependencies",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                sql: "([DependencyManifestVersion]=0 AND [DependencyManifest] IS NULL) OR ([DependencyManifestVersion]=1 AND [DependencyManifest] IS NOT NULL AND DATALENGTH([DependencyManifest]) BETWEEN 218 AND 6902)");
            migrationBuilder.Sql("""
                DENY UPDATE ON OBJECT::[aioffice].[GroupWorkCommitReceipts] ([DependencyManifestVersion], [DependencyManifest]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Committed dependency manifests require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
