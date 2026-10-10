using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpandGroupAutomaticNoteCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupWorkCommitReceipts_Counts",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupNotesCommittedOutbox_Values",
                schema: "aioffice",
                table: "GroupNotesCommittedOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupNotesCommittedItems_Values",
                schema: "aioffice",
                table: "GroupNotesCommittedItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupCustomerRequests_Values",
                schema: "aioffice",
                table: "GroupCustomerRequests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupWorkCommitReceipts_Counts",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                sql: "[SelectedMessageCount] BETWEEN 1 AND 100 AND (([Outcome]=2 AND [NoteCount]=0) OR ([Outcome] IN (1,3) AND [NoteCount] BETWEEN 1 AND 40))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupNotesCommittedOutbox_Values",
                schema: "aioffice",
                table: "GroupNotesCommittedOutbox",
                sql: "[NoteCount] BETWEEN 1 AND 40 AND [PublishAttempts]>=0 AND DATEPART(tz,[CommittedAtUtc])=0 AND DATEPART(tz,[AvailableAtUtc])=0 AND [AvailableAtUtc]>=[CommittedAtUtc] AND ([PublishedAtUtc] IS NULL OR (DATEPART(tz,[PublishedAtUtc])=0 AND [PublishedAtUtc]>=[CommittedAtUtc]))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupNotesCommittedItems_Values",
                schema: "aioffice",
                table: "GroupNotesCommittedItems",
                sql: "[Ordinal] BETWEEN 1 AND 40 AND [RequestRevision]>0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupCustomerRequests_Values",
                schema: "aioffice",
                table: "GroupCustomerRequests",
                sql: "[OriginCandidateOrdinal] BETWEEN 1 AND 40 AND [Kind] IN (1,2,3,4,5) AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [CurrentRevision]>0 AND [BusinessVersion]>0 AND [BusinessStatus] IN (1,2,3,4,5)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Committed combined note ordinals require reviewed forward repair; a narrowing rollback is not supported.");
        }
    }
}
