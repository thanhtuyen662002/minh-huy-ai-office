using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupHostAttentionOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupRequestRevisions_Origin",
                schema: "aioffice",
                table: "GroupRequestRevisions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupRequestRevisions_Origin",
                schema: "aioffice",
                table: "GroupRequestRevisions",
                sql: "([Origin]=1 AND [VerificationLevel]=1 AND [AuthorServiceId] IS NOT NULL AND [AuthorUserId] IS NULL AND [SourceBatchId] IS NOT NULL AND [ClaimEpoch] IS NOT NULL AND [ClaimEpoch]>0) OR ([Origin]=2 AND [VerificationLevel]=2 AND [AuthorServiceId] IS NULL AND [AuthorUserId] IS NOT NULL AND [SourceBatchId] IS NULL AND [ClaimEpoch] IS NULL) OR ([Origin]=3 AND [VerificationLevel]=3 AND [AuthorServiceId] IS NOT NULL AND [AuthorUserId] IS NULL AND [SourceBatchId] IS NOT NULL AND [ClaimEpoch] IS NOT NULL AND [ClaimEpoch]>0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Committed host attention revisions require reviewed forward repair; a narrowing rollback is not supported.");
        }
    }
}
