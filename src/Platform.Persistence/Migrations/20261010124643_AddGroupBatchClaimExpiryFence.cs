using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupBatchClaimExpiryFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiryObservedAtUtc",
                schema: "aioffice",
                table: "GroupBatchClaimStates",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupBatchClaimStates_ExpiryObservation",
                schema: "aioffice",
                table: "GroupBatchClaimStates",
                sql: "[ExpiryObservedAtUtc] IS NULL OR ([ExpiryObservedAtUtc] >= [ExpiresAtUtc] AND DATEPART(tz,[ExpiryObservedAtUtc]) = 0)");
            migrationBuilder.Sql("""
                GRANT UPDATE ON OBJECT::[aioffice].[GroupBatchClaimStates] ([ExpiryObservedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Committed group expiry observations require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
