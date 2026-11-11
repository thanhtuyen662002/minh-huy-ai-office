using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantBotLastRunUpdate : Migration
    {
        // SQL Server keeps a login's cached permission checks across ADD COLUMN, so a runtime
        // login that already wrote BotStatuses was refused UPDATE on the new LastRunJson column
        // (error 230) even though the table-level grant covers it. An explicit column grant
        // matches the table grant and invalidates that cache.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT UPDATE ON OBJECT::[aioffice].[BotStatuses] ([LastRunJson]) TO [aioffice_binding_runtime];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE UPDATE ON OBJECT::[aioffice].[BotStatuses] ([LastRunJson]) TO [aioffice_binding_runtime];");
        }
    }
}
