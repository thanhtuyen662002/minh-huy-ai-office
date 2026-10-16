using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpandOriginalInputPlanV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupAutomaticInputPlans_Format",
                schema: "aioffice",
                table: "GroupAutomaticInputPlans");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupAutomaticInputPlans_Format",
                schema: "aioffice",
                table: "GroupAutomaticInputPlans",
                sql: "[FormatVersion] IN (2,3) AND DATALENGTH([Plan]) BETWEEN 256 AND 32000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Stored original input V3 requires reviewed forward repair; format contract rollback is not supported.");
        }
    }
}
