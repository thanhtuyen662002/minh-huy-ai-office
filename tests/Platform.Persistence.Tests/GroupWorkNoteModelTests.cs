using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupWorkNoteModelTests
{
    private static readonly Type[] Types = [typeof(GroupCustomerRequestRecord), typeof(GroupRequestRevisionRecord),
        typeof(GroupRequestEvidenceRecord), typeof(GroupWorkCommitReceiptRecord), typeof(GroupWorkSourceDispositionRecord),
        typeof(GroupNotesCommittedOutboxRecord), typeof(GroupNotesCommittedItemRecord), typeof(GroupEditorGrantRecord),
        typeof(GroupGlossaryEntryRecord), typeof(GroupGlossaryRevisionRecord)];
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=localhost;Database=Group_Model;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void EveryNewRelationshipPreservesScopeAndEvidenceCannotCascadeOrBecomePortalTask()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        foreach (var type in Types)
        {
            var entity = model.FindEntityType(type)!;
            Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId" }, entity.FindPrimaryKey()!.Properties.Take(3).Select(x => x.Name));
            Assert.DoesNotContain(entity.GetProperties(), p => p.IsShadowProperty());
            foreach (var foreign in entity.GetForeignKeys())
            {
                Assert.Equal(new[] { "TenantId", "CompanyId" }, foreign.Properties.Take(2).Select(x => x.Name));
                Assert.Equal(DeleteBehavior.Restrict, foreign.DeleteBehavior);
                Assert.NotEqual(typeof(TaskRecord), foreign.PrincipalEntityType.ClrType);
                if (foreign.PrincipalEntityType.ClrType.Name.StartsWith("Group", StringComparison.Ordinal)
                    && foreign.PrincipalEntityType.ClrType != typeof(GroupServiceRecord))
                    Assert.Equal("BindingId", foreign.Properties[2].Name);
            }
        }
    }

    [Theory]
    [InlineData(typeof(GroupRequestRevisionRecord))]
    [InlineData(typeof(GroupGlossaryRevisionRecord))]
    public void PrivatePayloadHasFiniteEnvelopeAndNoPlaintextOrPlaintextFingerprint(Type type)
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(type)!;
        Assert.Equal(64029, entity.FindProperty("ProtectedContent")!.GetMaxLength());
        Assert.Equal(64, entity.FindProperty("ContentKeyId")!.GetMaxLength());
        Assert.Equal("Latin1_General_100_BIN2", entity.FindProperty("ContentKeyId")!.GetCollation());
        Assert.Equal(64, entity.FindProperty("EnvelopeSha256")!.GetMaxLength());
        foreach (var name in new[] { "Text", "Title", "Problem", "Quote", "RequestedDeadlineText", "ContentSha256", "PayloadJson", "TaskId", "DestinationId" })
            Assert.Null(entity.FindProperty(name));
        Assert.Contains(entity.GetCheckConstraints(), x => x.Sql.Contains("DATALENGTH([ProtectedContent]) BETWEEN 30 AND 64029", StringComparison.Ordinal));
        Assert.Contains(entity.GetCheckConstraints(), x => x.Sql.Contains("[DeletionGeneration]>=0", StringComparison.Ordinal));
    }

    [Fact]
    public void UnconfirmedNewBusinessRecordCannotAcquireResolvedStatusAssigneeOrItSlaByDefault()
    {
        var head = new GroupCustomerRequestRecord();
        Assert.Equal(GroupNoteBusinessStatus.New, head.BusinessStatus);
        Assert.Equal(1, head.BusinessVersion); Assert.Equal(1, head.CurrentRevision);
        Assert.Null(head.AssignedToUserId); Assert.Null(head.CommittedDueAtUtc);
        Assert.Null(head.ConfirmedByUserId); Assert.Null(head.ConfirmedAtUtc);
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupCustomerRequestRecord))!;
        Assert.True(entity.FindProperty("BusinessVersion")!.IsConcurrencyToken);
        Assert.True(entity.FindProperty("CurrentRevision")!.IsConcurrencyToken);
        var check = entity.GetCheckConstraints().Single(x => x.Name == "CK_GroupCustomerRequests_ITConfirmation").Sql;
        Assert.Contains("[ConfirmedByUserId] IS NOT NULL AND [ConfirmedAtUtc] IS NOT NULL", check);
        Assert.Contains("[BusinessStatus] IN (1,3) AND [AssignedToUserId] IS NULL AND [CommittedDueAtUtc] IS NULL", check);
        var revision = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupRequestRevisionRecord))!;
        var origin = revision.GetCheckConstraints().Single(x => x.Name == "CK_GroupRequestRevisions_Origin").Sql;
        Assert.Contains("[Origin]=1 AND [VerificationLevel]=1", origin);
        Assert.Contains("[ClaimEpoch] IS NOT NULL AND [ClaimEpoch]>0", origin); // SQL CHECK must not pass UNKNOWN for null.
        Assert.Contains("[AuthorUserId] IS NULL", origin);
        Assert.Contains("[Origin]=2 AND [VerificationLevel]=2", origin);
    }

    [Fact]
    public void LineageOriginalReceiptSelectedSourceAndOutboxUniquenessAreScoped()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var head = model.FindEntityType(typeof(GroupCustomerRequestRecord))!;
        Assert.Contains(head.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(
            new[] { "TenantId", "CompanyId", "BindingId", "OriginBatchId", "OriginOperationId", "OriginCandidateOrdinal" }));
        Assert.Contains(head.GetIndexes(), x => x.IsUnique && x.Properties.Last().Name == "RequestCode");
        var receipt = model.FindEntityType(typeof(GroupWorkCommitReceiptRecord))!;
        Assert.Contains(receipt.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "OperationId" }));
        Assert.Contains(receipt.GetCheckConstraints(), x => x.Sql.Contains("[Outcome]=2 AND [NoteCount]=0", StringComparison.Ordinal));
        Assert.Contains(receipt.GetCheckConstraints(), x => x.Sql.Contains("[SelectedMessageCount] BETWEEN 1 AND 100", StringComparison.Ordinal));
        var source = model.FindEntityType(typeof(GroupWorkSourceDispositionRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "MessageId" }, source.FindPrimaryKey()!.Properties.Select(x => x.Name));
        var outbox = model.FindEntityType(typeof(GroupNotesCommittedOutboxRecord))!;
        Assert.Contains(outbox.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" }));
        foreach (var name in new[] { "PayloadJson", "Body", "Title", "DestinationId", "UserId" }) Assert.Null(outbox.FindProperty(name));
        Assert.Contains(model.FindEntityType(typeof(GroupNotesCommittedItemRecord))!.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupRequestRevisionRecord));
    }

    [Fact]
    public void AudienceAndEditorAreDistinctAndGlossaryNeedsExplicitOperatorPublication()
    {
        var editor = new GroupEditorGrantRecord(); var glossary = new GroupGlossaryEntryRecord();
        Assert.False(editor.IsEnabled); Assert.False(glossary.IsEnabled); Assert.False(glossary.AllowExtraction);
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var grant = model.FindEntityType(typeof(GroupEditorGrantRecord))!;
        Assert.NotEqual(model.FindEntityType(typeof(GroupReaderGrantRecord)), grant);
        Assert.DoesNotContain(grant.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupReaderGrantRecord));
        Assert.Contains(grant.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(CompanyMembershipRecord));
        Assert.Contains(model.FindEntityType(typeof(GroupGlossaryRevisionRecord))!.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupGlossaryEntryRecord));
    }

    [Fact]
    public void AdditiveMigrationFreezesOperatorOwnershipEffectiveRuntimeRightsAndNoDestructiveRollback()
    {
        using var db = Database(); var migration = new AddGroupWorkNotes();
        Assert.Equal(10, migration.UpOperations.OfType<CreateTableOperation>().Count());
        Assert.DoesNotContain(migration.UpOperations, x => x is DropTableOperation or DropColumnOperation or AlterColumnOperation);
        var model = db.GetService<IDesignTimeModel>().Model;
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model).Select(x => x.CommandText));
        foreach (var type in Types)
        {
            var table = model.FindEntityType(type)!.GetTableName();
            Assert.Contains($"ALTER AUTHORIZATION ON OBJECT::[aioffice].[{table}] TO [aioffice_binding_operator_owner]", sql);
            Assert.Contains($"DENY UPDATE ON OBJECT::[aioffice].[{table}] ([TenantId], [CompanyId], [BindingId]", sql);
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'SELECT')=1", GroupWorkNotePermissionVerifier.VerificationSql);
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'UPDATE',c.name,N'COLUMN')", GroupWorkNotePermissionVerifier.VerificationSql);
        }
        foreach (var table in new[] { "GroupEditorGrants", "GroupGlossaryEntries", "GroupGlossaryRevisions" })
        {
            Assert.Contains($"GRANT SELECT ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'INSERT')=0", GroupWorkNotePermissionVerifier.VerificationSql);
        }
        foreach (var table in new[] { "GroupRequestRevisions", "GroupRequestEvidence", "GroupWorkCommitReceipts", "GroupWorkSourceDispositions", "GroupNotesCommittedItems" })
        {
            Assert.Contains($"GRANT SELECT, INSERT ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'UPDATE')=0", GroupWorkNotePermissionVerifier.VerificationSql);
        }
        Assert.Contains("GRANT UPDATE ON OBJECT::[aioffice].[GroupCustomerRequests] ([CurrentRevision], [BusinessStatus], [BusinessVersion], [AssignedToUserId], [CommittedDueAtUtc], [ConfirmedByUserId], [ConfirmedAtUtc], [UpdatedAtUtc])", sql);
        Assert.Contains("GRANT UPDATE ON OBJECT::[aioffice].[GroupNotesCommittedOutbox] ([AvailableAtUtc], [PublishAttempts], [PublishedAtUtc])", sql);
        Assert.DoesNotContain("DROP TABLE", sql); Assert.DoesNotContain("ALTER TABLE [aioffice].[Tasks]", sql);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Fact]
    public void GeneratedMigrationAndEffectivePermissionProofParseAsSqlServer160()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>().Generate(new AddGroupWorkNotes().UpOperations, model).Select(x => x.CommandText));
        foreach (var text in new[] { sql, GroupWorkNotePermissionVerifier.VerificationSql })
        {
            using var reader = new StringReader(text);
            new TSql160Parser(true).Parse(reader, out var errors);
            Assert.Empty(errors);
        }
    }
}
