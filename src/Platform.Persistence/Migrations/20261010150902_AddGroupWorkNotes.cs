using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupWorkNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupEditorGrants",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupEditorGrants", x => new { x.TenantId, x.CompanyId, x.BindingId, x.UserId });
                    table.CheckConstraint("CK_GroupEditorGrants_Version", "[Version]>0");
                    table.ForeignKey(
                        name: "FK_GroupEditorGrants_CompanyMemberships_TenantId_CompanyId_UserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.UserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupEditorGrants_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupGlossaryEntries",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentRevision = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AllowExtraction = table.Column<bool>(type: "bit", nullable: false),
                    PublishedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupGlossaryEntries", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
                    table.CheckConstraint("CK_GroupGlossaryEntries_Values", "[CurrentRevision]>0 AND [Version]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND DATEPART(tz,[CreatedAtUtc])=0");
                    table.ForeignKey(
                        name: "FK_GroupGlossaryEntries_CompanyMemberships_TenantId_CompanyId_PublishedByUserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.PublishedByUserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupGlossaryEntries_GroupBindings_TenantId_CompanyId_BindingId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBindings",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupWorkCommitReceipts",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSetSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SelectedMessageCount = table.Column<int>(type: "int", nullable: false),
                    NoteCount = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimEpoch = table.Column<long>(type: "bigint", nullable: false),
                    CredentialEpoch = table.Column<long>(type: "bigint", nullable: false),
                    GrantVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AccountVersion = table.Column<long>(type: "bigint", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupWorkCommitReceipts", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId });
                    table.CheckConstraint("CK_GroupWorkCommitReceipts_Authority", "[ClaimEpoch]>0 AND [CredentialEpoch]>0 AND [GrantVersion]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [AccountVersion]>0 AND DATEPART(tz,[CommittedAtUtc])=0");
                    table.CheckConstraint("CK_GroupWorkCommitReceipts_Counts", "[SelectedMessageCount] BETWEEN 1 AND 100 AND (([Outcome]=2 AND [NoteCount]=0) OR ([Outcome] IN (1,3) AND [NoteCount] BETWEEN 1 AND 20))");
                    table.CheckConstraint("CK_GroupWorkCommitReceipts_SourceSet", "DATALENGTH([SourceSetSha256])=64 AND [SourceSetSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.ForeignKey(
                        name: "FK_GroupWorkCommitReceipts_GroupBatchAllocations_TenantId_CompanyId_BindingId_BatchId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchAllocations",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupWorkCommitReceipts_GroupServices_TenantId_CompanyId_ServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupGlossaryRevisions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    PublishedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentKeyId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProtectedContent = table.Column<byte[]>(type: "varbinary(max)", maxLength: 64029, nullable: false),
                    EnvelopeSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupGlossaryRevisions", x => new { x.TenantId, x.CompanyId, x.BindingId, x.EntryId, x.Revision });
                    table.CheckConstraint("CK_GroupGlossaryRevisions_Payload", "DATALENGTH([ProtectedContent]) BETWEEN 30 AND 64029 AND DATALENGTH([ContentKeyId]) BETWEEN 1 AND 64 AND [ContentKeyId] NOT LIKE '%[^0-9A-Za-z_-]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([EnvelopeSha256])=64 AND [EnvelopeSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupGlossaryRevisions_Values", "[Revision]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND DATEPART(tz,[CreatedAtUtc])=0");
                    table.ForeignKey(
                        name: "FK_GroupGlossaryRevisions_CompanyMemberships_TenantId_CompanyId_PublishedByUserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.PublishedByUserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupGlossaryRevisions_GroupGlossaryEntries_TenantId_CompanyId_BindingId_EntryId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.EntryId },
                        principalSchema: "aioffice",
                        principalTable: "GroupGlossaryEntries",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupCustomerRequests",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginCandidateOrdinal = table.Column<int>(type: "int", nullable: false),
                    RequestCode = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    CurrentRevision = table.Column<long>(type: "bigint", nullable: false),
                    BusinessStatus = table.Column<int>(type: "int", nullable: false),
                    BusinessVersion = table.Column<long>(type: "bigint", nullable: false),
                    AssignedToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CommittedDueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfirmedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupCustomerRequests", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
                    table.CheckConstraint("CK_GroupCustomerRequests_Code", "DATALENGTH([RequestCode])=36 AND LEFT([RequestCode],4)='REQ-' AND SUBSTRING([RequestCode],5,32) NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupCustomerRequests_ITConfirmation", "([ConfirmedByUserId] IS NOT NULL AND [ConfirmedAtUtc] IS NOT NULL) OR ([ConfirmedByUserId] IS NULL AND [ConfirmedAtUtc] IS NULL AND [BusinessStatus] IN (1,3) AND [AssignedToUserId] IS NULL AND [CommittedDueAtUtc] IS NULL)");
                    table.CheckConstraint("CK_GroupCustomerRequests_Times", "DATEPART(tz,[CreatedAtUtc])=0 AND DATEPART(tz,[UpdatedAtUtc])=0 AND [UpdatedAtUtc]>=[CreatedAtUtc] AND ([CommittedDueAtUtc] IS NULL OR DATEPART(tz,[CommittedDueAtUtc])=0) AND ([ConfirmedAtUtc] IS NULL OR (DATEPART(tz,[ConfirmedAtUtc])=0 AND [ConfirmedAtUtc]>=[CreatedAtUtc]))");
                    table.CheckConstraint("CK_GroupCustomerRequests_Values", "[OriginCandidateOrdinal] BETWEEN 1 AND 20 AND [Kind] IN (1,2,3,4,5) AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND [CurrentRevision]>0 AND [BusinessVersion]>0 AND [BusinessStatus] IN (1,2,3,4,5)");
                    table.ForeignKey(
                        name: "FK_GroupCustomerRequests_CompanyMemberships_TenantId_CompanyId_AssignedToUserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.AssignedToUserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupCustomerRequests_CompanyMemberships_TenantId_CompanyId_ConfirmedByUserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ConfirmedByUserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupCustomerRequests_GroupWorkCommitReceipts_TenantId_CompanyId_BindingId_OriginBatchId_OriginOperationId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.OriginBatchId, x.OriginOperationId },
                        principalSchema: "aioffice",
                        principalTable: "GroupWorkCommitReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupNotesCommittedOutbox",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NoteCount = table.Column<int>(type: "int", nullable: false),
                    IsHistoricalBackfill = table.Column<bool>(type: "bit", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishAttempts = table.Column<int>(type: "int", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupNotesCommittedOutbox", x => new { x.TenantId, x.CompanyId, x.BindingId, x.Id });
                    table.CheckConstraint("CK_GroupNotesCommittedOutbox_Values", "[NoteCount] BETWEEN 1 AND 20 AND [PublishAttempts]>=0 AND DATEPART(tz,[CommittedAtUtc])=0 AND DATEPART(tz,[AvailableAtUtc])=0 AND [AvailableAtUtc]>=[CommittedAtUtc] AND ([PublishedAtUtc] IS NULL OR (DATEPART(tz,[PublishedAtUtc])=0 AND [PublishedAtUtc]>=[CommittedAtUtc]))");
                    table.ForeignKey(
                        name: "FK_GroupNotesCommittedOutbox_GroupWorkCommitReceipts_TenantId_CompanyId_BindingId_BatchId_OperationId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId },
                        principalSchema: "aioffice",
                        principalTable: "GroupWorkCommitReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupWorkSourceDispositions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageRevision = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupWorkSourceDispositions", x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.MessageId });
                    table.CheckConstraint("CK_GroupWorkSourceDispositions_Values", "[MessageRevision]>0 AND [Outcome] IN (1,2,3,4,5,6,7,8)");
                    table.ForeignKey(
                        name: "FK_GroupWorkSourceDispositions_GroupMessageRevisions_TenantId_CompanyId_BindingId_MessageId_MessageRevision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.MessageRevision },
                        principalSchema: "aioffice",
                        principalTable: "GroupMessageRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupWorkSourceDispositions_GroupWorkCommitReceipts_TenantId_CompanyId_BindingId_BatchId_OperationId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.OperationId },
                        principalSchema: "aioffice",
                        principalTable: "GroupWorkCommitReceipts",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupRequestRevisions",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    VerificationLevel = table.Column<int>(type: "int", nullable: false),
                    AuthorServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimEpoch = table.Column<long>(type: "bigint", nullable: true),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ContentKeyId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProtectedContent = table.Column<byte[]>(type: "varbinary(max)", maxLength: 64029, nullable: false),
                    EnvelopeSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupRequestRevisions", x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.Revision });
                    table.CheckConstraint("CK_GroupRequestRevisions_Origin", "([Origin]=1 AND [VerificationLevel]=1 AND [AuthorServiceId] IS NOT NULL AND [AuthorUserId] IS NULL AND [SourceBatchId] IS NOT NULL AND [ClaimEpoch] IS NOT NULL AND [ClaimEpoch]>0) OR ([Origin]=2 AND [VerificationLevel]=2 AND [AuthorServiceId] IS NULL AND [AuthorUserId] IS NOT NULL AND [SourceBatchId] IS NULL AND [ClaimEpoch] IS NULL)");
                    table.CheckConstraint("CK_GroupRequestRevisions_Payload", "DATALENGTH([ProtectedContent]) BETWEEN 30 AND 64029 AND DATALENGTH([ContentKeyId]) BETWEEN 1 AND 64 AND [ContentKeyId] NOT LIKE '%[^0-9A-Za-z_-]%' COLLATE Latin1_General_100_BIN2 AND DATALENGTH([EnvelopeSha256])=64 AND [EnvelopeSha256] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_GroupRequestRevisions_Values", "[Revision]>0 AND [SourceVersion]>0 AND [DeletionGeneration]>=0 AND DATEPART(tz,[CreatedAtUtc])=0");
                    table.ForeignKey(
                        name: "FK_GroupRequestRevisions_CompanyMemberships_TenantId_CompanyId_AuthorUserId",
                        columns: x => new { x.TenantId, x.CompanyId, x.AuthorUserId },
                        principalSchema: "aioffice",
                        principalTable: "CompanyMemberships",
                        principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupRequestRevisions_GroupBatchAllocations_TenantId_CompanyId_BindingId_SourceBatchId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.SourceBatchId },
                        principalSchema: "aioffice",
                        principalTable: "GroupBatchAllocations",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupRequestRevisions_GroupCustomerRequests_TenantId_CompanyId_BindingId_RequestId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId },
                        principalSchema: "aioffice",
                        principalTable: "GroupCustomerRequests",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupRequestRevisions_GroupServices_TenantId_CompanyId_AuthorServiceId",
                        columns: x => new { x.TenantId, x.CompanyId, x.AuthorServiceId },
                        principalSchema: "aioffice",
                        principalTable: "GroupServices",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupNotesCommittedItems",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestRevision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupNotesCommittedItems", x => new { x.TenantId, x.CompanyId, x.BindingId, x.OutboxId, x.Ordinal });
                    table.CheckConstraint("CK_GroupNotesCommittedItems_Values", "[Ordinal] BETWEEN 1 AND 20 AND [RequestRevision]>0");
                    table.ForeignKey(
                        name: "FK_GroupNotesCommittedItems_GroupNotesCommittedOutbox_TenantId_CompanyId_BindingId_OutboxId",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.OutboxId },
                        principalSchema: "aioffice",
                        principalTable: "GroupNotesCommittedOutbox",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupNotesCommittedItems_GroupRequestRevisions_TenantId_CompanyId_BindingId_RequestId_RequestRevision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.RequestRevision },
                        principalSchema: "aioffice",
                        principalTable: "GroupRequestRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupRequestEvidence",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestRevision = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageRevision = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupRequestEvidence", x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.RequestRevision, x.Ordinal });
                    table.CheckConstraint("CK_GroupRequestEvidence_Values", "[RequestRevision]>0 AND [Ordinal] BETWEEN 1 AND 10 AND [MessageRevision]>0 AND [Kind] IN (1,2)");
                    table.ForeignKey(
                        name: "FK_GroupRequestEvidence_GroupMessageRevisions_TenantId_CompanyId_BindingId_MessageId_MessageRevision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.MessageRevision },
                        principalSchema: "aioffice",
                        principalTable: "GroupMessageRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupRequestEvidence_GroupRequestRevisions_TenantId_CompanyId_BindingId_RequestId_RequestRevision",
                        columns: x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.RequestRevision },
                        principalSchema: "aioffice",
                        principalTable: "GroupRequestRevisions",
                        principalColumns: new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupCustomerRequests_TenantId_CompanyId_AssignedToUserId",
                schema: "aioffice",
                table: "GroupCustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "AssignedToUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupCustomerRequests_TenantId_CompanyId_BindingId_DeletionGeneration_CreatedAtUtc_Id",
                schema: "aioffice",
                table: "GroupCustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "DeletionGeneration", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupCustomerRequests_TenantId_CompanyId_BindingId_OriginBatchId_OriginOperationId_OriginCandidateOrdinal",
                schema: "aioffice",
                table: "GroupCustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OriginBatchId", "OriginOperationId", "OriginCandidateOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupCustomerRequests_TenantId_CompanyId_BindingId_RequestCode",
                schema: "aioffice",
                table: "GroupCustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "RequestCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupCustomerRequests_TenantId_CompanyId_ConfirmedByUserId",
                schema: "aioffice",
                table: "GroupCustomerRequests",
                columns: new[] { "TenantId", "CompanyId", "ConfirmedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupEditorGrants_TenantId_CompanyId_UserId",
                schema: "aioffice",
                table: "GroupEditorGrants",
                columns: new[] { "TenantId", "CompanyId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupGlossaryEntries_TenantId_CompanyId_PublishedByUserId",
                schema: "aioffice",
                table: "GroupGlossaryEntries",
                columns: new[] { "TenantId", "CompanyId", "PublishedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupGlossaryRevisions_TenantId_CompanyId_PublishedByUserId",
                schema: "aioffice",
                table: "GroupGlossaryRevisions",
                columns: new[] { "TenantId", "CompanyId", "PublishedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupNotesCommittedItems_TenantId_CompanyId_BindingId_OutboxId_RequestId_RequestRevision",
                schema: "aioffice",
                table: "GroupNotesCommittedItems",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OutboxId", "RequestId", "RequestRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupNotesCommittedItems_TenantId_CompanyId_BindingId_RequestId_RequestRevision",
                schema: "aioffice",
                table: "GroupNotesCommittedItems",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "RequestRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupNotesCommittedOutbox_TenantId_CompanyId_BindingId_BatchId_OperationId",
                schema: "aioffice",
                table: "GroupNotesCommittedOutbox",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupNotesCommittedOutbox_TenantId_CompanyId_BindingId_PublishedAtUtc_AvailableAtUtc",
                schema: "aioffice",
                table: "GroupNotesCommittedOutbox",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "PublishedAtUtc", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupRequestEvidence_TenantId_CompanyId_BindingId_MessageId_MessageRevision",
                schema: "aioffice",
                table: "GroupRequestEvidence",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "MessageRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupRequestRevisions_TenantId_CompanyId_AuthorServiceId",
                schema: "aioffice",
                table: "GroupRequestRevisions",
                columns: new[] { "TenantId", "CompanyId", "AuthorServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupRequestRevisions_TenantId_CompanyId_AuthorUserId",
                schema: "aioffice",
                table: "GroupRequestRevisions",
                columns: new[] { "TenantId", "CompanyId", "AuthorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupRequestRevisions_TenantId_CompanyId_BindingId_SourceBatchId",
                schema: "aioffice",
                table: "GroupRequestRevisions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "SourceBatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupWorkCommitReceipts_TenantId_CompanyId_BindingId_OperationId",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupWorkCommitReceipts_TenantId_CompanyId_ServiceId",
                schema: "aioffice",
                table: "GroupWorkCommitReceipts",
                columns: new[] { "TenantId", "CompanyId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupWorkSourceDispositions_TenantId_CompanyId_BindingId_BatchId_OperationId",
                schema: "aioffice",
                table: "GroupWorkSourceDispositions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupWorkSourceDispositions_TenantId_CompanyId_BindingId_MessageId_MessageRevision",
                schema: "aioffice",
                table: "GroupWorkSourceDispositions",
                columns: new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "MessageRevision" });
            migrationBuilder.Sql("""
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupCustomerRequests] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupCustomerRequests] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupCustomerRequests] TO [aioffice_binding_runtime];
                GRANT UPDATE ON OBJECT::[aioffice].[GroupCustomerRequests] ([CurrentRevision], [BusinessStatus], [BusinessVersion], [AssignedToUserId], [CommittedDueAtUtc], [ConfirmedByUserId], [ConfirmedAtUtc], [UpdatedAtUtc]) TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupCustomerRequests] ([TenantId], [CompanyId], [BindingId], [Id], [OriginBatchId], [OriginOperationId], [OriginCandidateOrdinal], [RequestCode], [Kind], [SourceVersion], [DeletionGeneration], [CreatedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupRequestRevisions] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupRequestRevisions] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupRequestRevisions] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupRequestRevisions] ([TenantId], [CompanyId], [BindingId], [RequestId], [Revision], [Origin], [VerificationLevel], [AuthorServiceId], [AuthorUserId], [SourceBatchId], [ClaimEpoch], [SourceVersion], [DeletionGeneration], [ContentKeyId], [ProtectedContent], [EnvelopeSha256], [CreatedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupRequestEvidence] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupRequestEvidence] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupRequestEvidence] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupRequestEvidence] ([TenantId], [CompanyId], [BindingId], [RequestId], [RequestRevision], [Ordinal], [MessageId], [MessageRevision], [Kind]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupWorkCommitReceipts] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupWorkCommitReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupWorkCommitReceipts] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupWorkCommitReceipts] ([TenantId], [CompanyId], [BindingId], [BatchId], [OperationId], [SourceSetSha256], [SelectedMessageCount], [NoteCount], [Outcome], [ServiceId], [ClaimEpoch], [CredentialEpoch], [GrantVersion], [SourceVersion], [DeletionGeneration], [AccountVersion], [CommittedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupWorkSourceDispositions] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupWorkSourceDispositions] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupWorkSourceDispositions] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupWorkSourceDispositions] ([TenantId], [CompanyId], [BindingId], [BatchId], [MessageId], [MessageRevision], [OperationId], [Outcome]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupNotesCommittedOutbox] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupNotesCommittedOutbox] TO [aioffice_binding_runtime];
                DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupNotesCommittedOutbox] TO [aioffice_binding_runtime];
                GRANT UPDATE ON OBJECT::[aioffice].[GroupNotesCommittedOutbox] ([AvailableAtUtc], [PublishAttempts], [PublishedAtUtc]) TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupNotesCommittedOutbox] ([TenantId], [CompanyId], [BindingId], [Id], [BatchId], [OperationId], [NoteCount], [IsHistoricalBackfill], [CommittedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupNotesCommittedItems] TO [aioffice_binding_operator_owner];
                GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupNotesCommittedItems] TO [aioffice_binding_runtime];
                DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupNotesCommittedItems] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupNotesCommittedItems] ([TenantId], [CompanyId], [BindingId], [OutboxId], [Ordinal], [RequestId], [RequestRevision]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupEditorGrants] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[GroupEditorGrants] TO [aioffice_binding_runtime];
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupEditorGrants] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupEditorGrants] ([TenantId], [CompanyId], [BindingId], [UserId], [Version], [IsEnabled]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupGlossaryEntries] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[GroupGlossaryEntries] TO [aioffice_binding_runtime];
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupGlossaryEntries] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupGlossaryEntries] ([TenantId], [CompanyId], [BindingId], [Id], [CurrentRevision], [Version], [SourceVersion], [DeletionGeneration], [IsEnabled], [AllowExtraction], [PublishedByUserId], [CreatedAtUtc]) TO [aioffice_binding_runtime];
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupGlossaryRevisions] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[GroupGlossaryRevisions] TO [aioffice_binding_runtime];
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[GroupGlossaryRevisions] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[GroupGlossaryRevisions] ([TenantId], [CompanyId], [BindingId], [EntryId], [Revision], [SourceVersion], [DeletionGeneration], [PublishedByUserId], [ContentKeyId], [ProtectedContent], [EnvelopeSha256], [CreatedAtUtc]) TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Committed group notes require reviewed forward repair; destructive rollback is not supported.");
        }
    }
}
