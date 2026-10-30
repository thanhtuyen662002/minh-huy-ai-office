using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBotMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BotMessages",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    GroupExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SenderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    HasImage = table.Column<bool>(type: "bit", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotMessages", x => new { x.TenantId, x.CompanyId, x.Channel, x.GroupExternalId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_BotMessages_BotChannels_TenantId_CompanyId_Channel",
                        columns: x => new { x.TenantId, x.CompanyId, x.Channel },
                        principalSchema: "aioffice",
                        principalTable: "BotChannels",
                        principalColumns: new[] { "TenantId", "CompanyId", "Channel" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotMessages_TenantId_CompanyId_Channel_GroupExternalId_SentAt",
                schema: "aioffice",
                table: "BotMessages",
                columns: new[] { "TenantId", "CompanyId", "Channel", "GroupExternalId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BotMessages_TenantId_CompanyId_SentAt",
                schema: "aioffice",
                table: "BotMessages",
                columns: new[] { "TenantId", "CompanyId", "SentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotMessages",
                schema: "aioffice");
        }
    }
}
