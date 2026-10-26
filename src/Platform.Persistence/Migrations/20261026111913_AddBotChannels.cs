using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBotChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BotAiSettings",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProtectedApiKey = table.Column<byte[]>(type: "varbinary(max)", maxLength: 8192, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotAiSettings", x => new { x.TenantId, x.CompanyId });
                    table.CheckConstraint("CK_BotAiSettings_Version", "[Version]>0");
                    table.ForeignKey(
                        name: "FK_BotAiSettings_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BotChannelAudits",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    After = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotChannelAudits", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_BotChannelAudits_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BotChannels",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    SummaryScheduleJson = table.Column<string>(type: "nvarchar(max)", maxLength: 15000, nullable: false),
                    TechGroupExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ConfigVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotChannels", x => new { x.TenantId, x.CompanyId, x.Channel });
                    table.CheckConstraint("CK_BotChannels_Channel", "[Channel] IN ('zalo','whatsapp')");
                    table.CheckConstraint("CK_BotChannels_Version", "[ConfigVersion]>0");
                    table.ForeignKey(
                        name: "FK_BotChannels_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BotCommands",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotCommands", x => new { x.TenantId, x.CompanyId, x.Channel, x.Id });
                    table.CheckConstraint("CK_BotCommands_Kind", "[Kind] IN ('summarize-now','relogin')");
                    table.ForeignKey(
                        name: "FK_BotCommands_BotChannels_TenantId_CompanyId_Channel",
                        columns: x => new { x.TenantId, x.CompanyId, x.Channel },
                        principalSchema: "aioffice",
                        principalTable: "BotChannels",
                        principalColumns: new[] { "TenantId", "CompanyId", "Channel" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BotCustomerGroups",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotCustomerGroups", x => new { x.TenantId, x.CompanyId, x.Channel, x.ExternalId });
                    table.ForeignKey(
                        name: "FK_BotCustomerGroups_BotChannels_TenantId_CompanyId_Channel",
                        columns: x => new { x.TenantId, x.CompanyId, x.Channel },
                        principalSchema: "aioffice",
                        principalTable: "BotChannels",
                        principalColumns: new[] { "TenantId", "CompanyId", "Channel" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BotStatuses",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LoginState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    GroupsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 500000, nullable: false),
                    LastSummaryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LoginQrImage = table.Column<byte[]>(type: "varbinary(max)", maxLength: 65536, nullable: true),
                    LoginQrContentType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LoginQrExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotStatuses", x => new { x.TenantId, x.CompanyId, x.Channel });
                    table.ForeignKey(
                        name: "FK_BotStatuses_BotChannels_TenantId_CompanyId_Channel",
                        columns: x => new { x.TenantId, x.CompanyId, x.Channel },
                        principalSchema: "aioffice",
                        principalTable: "BotChannels",
                        principalColumns: new[] { "TenantId", "CompanyId", "Channel" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotChannelAudits_TenantId_CompanyId_Timestamp",
                schema: "aioffice",
                table: "BotChannelAudits",
                columns: new[] { "TenantId", "CompanyId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_BotCommands_TenantId_CompanyId_Channel_ExpiresAt",
                schema: "aioffice",
                table: "BotCommands",
                columns: new[] { "TenantId", "CompanyId", "Channel", "ExpiresAt" });

            foreach (var table in new[] { "BotChannels", "BotAiSettings", "BotStatuses", "BotCommands" })
                migrationBuilder.Sql($"""
                    ALTER AUTHORIZATION ON OBJECT::[aioffice].[{table}] TO [aioffice_binding_operator_owner];
                    GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[{table}] TO [aioffice_binding_runtime];
                    DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table}] TO [aioffice_binding_runtime];
                    DENY UPDATE ON OBJECT::[aioffice].[{table}] ([TenantId], [CompanyId]) TO [aioffice_binding_runtime];
                    """);
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[BotCustomerGroups] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::[aioffice].[BotCustomerGroups] TO [aioffice_binding_runtime];
                DENY ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[BotCustomerGroups] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[BotCustomerGroups] ([TenantId], [CompanyId], [Channel], [ExternalId]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[BotChannelAudits] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[BotChannelAudits] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[BotChannelAudits] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[BotChannelAudits] ([TenantId], [CompanyId], [Id], [Channel], [ActorUserId], [Field], [Before], [After], [Timestamp]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotAiSettings",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "BotChannelAudits",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "BotCommands",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "BotCustomerGroups",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "BotStatuses",
                schema: "aioffice");

            migrationBuilder.DropTable(
                name: "BotChannels",
                schema: "aioffice");
        }
    }
}
