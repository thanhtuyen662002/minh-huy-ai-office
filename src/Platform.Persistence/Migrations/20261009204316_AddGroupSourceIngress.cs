using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupSourceIngress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupConnectorAccounts",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExternalAccountId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdentityHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PackageVersion = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    GitCommit = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    QualificationJson = table.Column<string>(type: "nvarchar(max)", maxLength: 16000, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupConnectorAccounts", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_GroupConnectorAccounts_Qualification", "ISJSON([QualificationJson]) = 1");
                    table.CheckConstraint("CK_GroupConnectorAccounts_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_GroupConnectorAccounts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupServices",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    CredentialReference = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupServices", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_GroupServices_Epoch", "[CredentialEpoch] > 0");
                    table.ForeignKey(
                        name: "FK_GroupServices_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupBindings",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExternalAccountId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExternalGroupId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdentityHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PhysicalGroupHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBindings", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_GroupBindings_Role", "[Role] IN (1,2)");
                    table.CheckConstraint("CK_GroupBindings_Versions", "[Version] > 0 AND [DeletionGeneration] >= 0");
                    table.ForeignKey(
                        name: "FK_GroupBindings_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupBindings_GroupConnectorAccounts_TenantId_CompanyId_ConnectorAccountId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ConnectorAccountId },
                        principalSchema: "aioffice",
                        principalTable: "GroupConnectorAccounts",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupListenerLeases",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupListenerLeases", x => new { x.TenantId, x.CompanyId, x.ConnectorAccountId });
                    table.CheckConstraint("CK_GroupListenerLeases_Values", "[Epoch] > 0 AND [ExpiresAtUtc] >= [HeartbeatAtUtc] AND DATEPART(tz,[ExpiresAtUtc]) = 0 AND DATEPART(tz,[HeartbeatAtUtc]) = 0");
                    table.ForeignKey(
                        name: "FK_GroupListenerLeases_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupListenerLeases_GroupConnectorAccounts_TenantId_CompanyId_ConnectorAccountId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ConnectorAccountId },
                        principalSchema: "aioffice",
                        principalTable: "GroupConnectorAccounts",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupCoverageGaps",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AfterCommittedSequence = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReconnectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupCoverageGaps", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
                    table.CheckConstraint("CK_GroupCoverageGaps_Values", "[AfterCommittedSequence] >= 0 AND DATEPART(tz,[OpenedAtUtc]) = 0 AND ([ReconnectedAtUtc] IS NULL OR ([ReconnectedAtUtc] >= [OpenedAtUtc] AND DATEPART(tz,[ReconnectedAtUtc]) = 0))");
                    table.ForeignKey(
                        name: "FK_GroupCoverageGaps_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupCoverageGaps_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupMessages",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalMessageId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdentityHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMessages", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
                    table.ForeignKey(
                        name: "FK_GroupMessages_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMessages_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupReaderGrants",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupReaderGrants", x => new { x.TenantId, x.CompanyId, x.UserId, x.BindingId });
                    table.CheckConstraint("CK_GroupReaderGrants_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_GroupReaderGrants_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupReaderGrants_CompanyMemberships_TenantId_CompanyId_UserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.UserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupReaderGrants_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupServiceGrants",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Capability = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupServiceGrants", x => new { x.TenantId, x.CompanyId, x.ServiceId, x.BindingId, x.Capability });
                    table.CheckConstraint("CK_GroupServiceGrants_Values", "[Version] > 0 AND [Capability] IN (1,2,3)");
                    table.ForeignKey(
                        name: "FK_GroupServiceGrants_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupServiceGrants_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupServiceGrants_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupSourceStates",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommittedSequence = table.Column<long>(type: "bigint", nullable: false),
                    FirstPendingAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastPendingAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ScheduledThroughSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupSourceStates", x => new { x.TenantId, x.CompanyId, x.BindingId });
                    table.CheckConstraint("CK_GroupSourceStates_Cursors", "[CommittedSequence] >= [ScheduledThroughSequence] AND [ScheduledThroughSequence] >= 0");
                    table.CheckConstraint("CK_GroupSourceStates_Pending", "([FirstPendingAtUtc] IS NULL AND [LastPendingAtUtc] IS NULL) OR ([FirstPendingAtUtc] IS NOT NULL AND [LastPendingAtUtc] IS NOT NULL AND [FirstPendingAtUtc] <= [LastPendingAtUtc] AND DATEPART(tz,[FirstPendingAtUtc]) = 0 AND DATEPART(tz,[LastPendingAtUtc]) = 0)");
                    table.ForeignKey(
                        name: "FK_GroupSourceStates_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupSourceStates_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupMessageRevisions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CommittedSequence = table.Column<long>(type: "bigint", nullable: false),
                    ExternalRevisionEventId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SenderId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReplyToMessageId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ContentKeyId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProtectedContent = table.Column<byte[]>(type: "varbinary(max)", maxLength: 65536, nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsHistoricalBackfill = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMessageRevisions", x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.Revision });
                    table.CheckConstraint("CK_GroupMessageRevisions_Content", "DATALENGTH([ProtectedContent]) BETWEEN 29 AND 65536");
                    table.CheckConstraint("CK_GroupMessageRevisions_Utc", "DATEPART(tz,[OccurredAtUtc]) = 0 AND DATEPART(tz,[CommittedAtUtc]) = 0");
                    table.CheckConstraint("CK_GroupMessageRevisions_Values", "[Revision] > 0 AND [CommittedSequence] > 0 AND [SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [Kind] IN (1,2,3,4)");
                    table.ForeignKey(
                        name: "FK_GroupMessageRevisions_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMessageRevisions_GroupMessages_TenantId_CompanyId_BindingId_MessageId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId },
                        principalSchema: "aioffice",
                        principalTable: "GroupMessages",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupIngressOutbox",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CommittedSequence = table.Column<long>(type: "bigint", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishAttempts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupIngressOutbox", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
                    table.CheckConstraint("CK_GroupIngressOutbox_Values", "[Revision] > 0 AND [CommittedSequence] > 0 AND [PublishAttempts] >= 0 AND DATEPART(tz,[AvailableAtUtc]) = 0 AND ([PublishedAtUtc] IS NULL OR DATEPART(tz,[PublishedAtUtc]) = 0)");
                    table.ForeignKey(
                        name: "FK_GroupIngressOutbox_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupIngressOutbox_GroupMessageRevisions_TenantId_CompanyId_BindingId_MessageId_Revision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.Revision },
                        principalSchema: "aioffice",
                        principalTable: "GroupMessageRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupIngressReceipts",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventIdentityHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExternalRevisionEventId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    EnvelopeSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ListenerEpoch = table.Column<long>(type: "bigint", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupIngressReceipts", x => new { x.TenantId, x.CompanyId, x.BindingId, x.EventIdentityHash });
                    table.CheckConstraint("CK_GroupIngressReceipts_Values", "[Revision] > 0 AND [CredentialEpoch] > 0 AND [ListenerEpoch] > 0 AND DATEPART(tz,[CommittedAtUtc]) = 0");
                    table.ForeignKey(
                        name: "FK_GroupIngressReceipts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupIngressReceipts_GroupMessageRevisions_TenantId_CompanyId_BindingId_MessageId_Revision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.Revision },
                        principalSchema: "aioffice",
                        principalTable: "GroupMessageRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupIngressReceipts_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupBindings_IdentityHash",
                schema: "aioffice",
                table: "GroupBindings",
                column: "IdentityHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBindings_PhysicalGroupHash",
                schema: "aioffice",
                table: "GroupBindings",
                column: "PhysicalGroupHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupBindings_TenantId_CompanyId_ConnectorAccountId",
                schema: "aioffice",
                table: "GroupBindings",
                columns: new[] { "TenantId", "CompanyId", "ConnectorAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupConnectorAccounts_IdentityHash",
                schema: "aioffice",
                table: "GroupConnectorAccounts",
                column: "IdentityHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupCoverageGaps_TenantId_CompanyId_BindingId_OpenedAtUtc",
                schema: "aioffice",
                table: "GroupCoverageGaps",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OpenedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupIngressOutbox_PublishedAtUtc_AvailableAtUtc",
                schema: "aioffice",
                table: "GroupIngressOutbox",
                columns: new[] { "PublishedAtUtc", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupIngressOutbox_TenantId_CompanyId_BindingId_MessageId_Revision",
                schema: "aioffice",
                table: "GroupIngressOutbox",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupIngressReceipts_TenantId_CompanyId_BindingId_MessageId_Revision",
                schema: "aioffice",
                table: "GroupIngressReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupIngressReceipts_TenantId_CompanyId_ServiceId",
                schema: "aioffice",
                table: "GroupIngressReceipts",
                columns: new[] { "TenantId", "CompanyId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessageRevisions_TenantId_CompanyId_BindingId_CommittedSequence",
                schema: "aioffice",
                table: "GroupMessageRevisions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "CommittedSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_TenantId_CompanyId_BindingId_IdentityHash",
                schema: "aioffice",
                table: "GroupMessages",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "IdentityHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupReaderGrants_TenantId_CompanyId_BindingId",
                schema: "aioffice",
                table: "GroupReaderGrants",
                columns: new[] { "TenantId", "CompanyId", "BindingId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupServiceGrants_TenantId_CompanyId_BindingId",
                schema: "aioffice",
                table: "GroupServiceGrants",
                columns: new[] { "TenantId", "CompanyId", "BindingId" });
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupConnectorAccounts] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[GroupConnectorAccounts] TO [aioffice_binding_runtime];
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupConnectorAccounts] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupConnectorAccounts] ([TenantId], [CompanyId], [Id], [Provider], [ExternalAccountId], [IdentityHash], [PackageVersion], [GitCommit], [QualificationJson], [Version], [IsEnabled]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupServices] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[GroupServices] TO [aioffice_binding_runtime];
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupServices] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupServices] ([TenantId], [CompanyId], [Id], [CredentialEpoch], [CredentialReference], [IsEnabled]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupBindings] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[GroupBindings] TO [aioffice_binding_runtime];
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupBindings] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupBindings] ([TenantId], [CompanyId], [Id], [ConnectorAccountId], [Role], [Provider], [ExternalAccountId], [ExternalGroupId], [IdentityHash], [PhysicalGroupHash], [DisplayName], [Version], [DeletionGeneration], [IsEnabled]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupServiceGrants] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[GroupServiceGrants] TO [aioffice_binding_runtime];
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupServiceGrants] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupServiceGrants] ([TenantId], [CompanyId], [ServiceId], [BindingId], [Capability], [Version], [IsEnabled]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupReaderGrants] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[GroupReaderGrants] TO [aioffice_binding_runtime];
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupReaderGrants] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupReaderGrants] ([TenantId], [CompanyId], [UserId], [BindingId], [Version], [IsEnabled]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupMessages] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupMessages] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupMessages] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupMessages] ([TenantId], [CompanyId], [BindingId], [Id], [ExternalMessageId], [IdentityHash]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupMessageRevisions] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupMessageRevisions] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupMessageRevisions] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupMessageRevisions] ([TenantId], [CompanyId], [BindingId], [MessageId], [Revision], [CommittedSequence], [ExternalRevisionEventId], [SenderId], [ReplyToMessageId], [Kind], [ContentSha256], [ContentKeyId], [ProtectedContent], [SourceVersion], [DeletionGeneration], [OccurredAtUtc], [CommittedAtUtc], [IsHistoricalBackfill]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupIngressReceipts] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupIngressReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupIngressReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupIngressReceipts] ([TenantId], [CompanyId], [BindingId], [EventIdentityHash], [ExternalRevisionEventId], [EnvelopeSha256], [MessageId], [Revision], [ServiceId], [CredentialEpoch], [ListenerEpoch], [CommittedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupListenerLeases] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[GroupListenerLeases] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupListenerLeases] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupListenerLeases] ([TenantId], [CompanyId], [ConnectorAccountId]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupSourceStates] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[GroupSourceStates] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupSourceStates] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupSourceStates] ([TenantId], [CompanyId], [BindingId]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupCoverageGaps] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[GroupCoverageGaps] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupCoverageGaps] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupCoverageGaps] ([TenantId], [CompanyId], [BindingId], [Id], [AfterCommittedSequence], [Reason], [OpenedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupIngressOutbox] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[GroupIngressOutbox] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupIngressOutbox] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupIngressOutbox] ([TenantId], [CompanyId], [BindingId], [Id], [MessageId], [Revision], [CommittedSequence]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Protected group source history requires reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
