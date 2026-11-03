using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RaiseKnowledgePageBodyLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Body is already nvarchar(max); only the validated length changed (20,000 to 500,000 characters).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
