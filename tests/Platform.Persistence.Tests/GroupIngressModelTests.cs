using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupIngressModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=localhost;Database=Group_Model;User Id=model;Password=model;TrustServerCertificate=true").Options);

    [Fact]
    public void EveryGroupRelationPreservesTenantCompanyAndCannotCascadeAwayEvidence()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var entities = model.GetEntityTypes().Where(x => x.ClrType.Name.StartsWith("Group", StringComparison.Ordinal)).ToArray();
        Assert.Equal(30, entities.Length);
        foreach (var entity in entities)
        {
            Assert.Equal(new[] { "TenantId", "CompanyId" }, entity.FindPrimaryKey()!.Properties.Take(2).Select(x => x.Name));
            foreach (var foreign in entity.GetForeignKeys())
            {
                Assert.Equal(new[] { "TenantId", "CompanyId" }, foreign.Properties.Take(2).Select(x => x.Name));
                Assert.Equal(DeleteBehavior.Restrict, foreign.DeleteBehavior);
                Assert.NotEqual(typeof(TaskRecord), foreign.PrincipalEntityType.ClrType);
            }
        }
    }

    [Fact]
    public void PhysicalGroupRolesHaveGlobalExclusivityAndOrdinalIdentityRemainsStored()
    {
        using var db = Database(); var binding = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupBindingRecord))!;
        Assert.Contains(binding.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "PhysicalGroupHash" }));
        Assert.Contains(binding.GetCheckConstraints(), x => x.Sql == "[Role] IN (1,2)");
        foreach (var name in new[] { "ExternalAccountId", "ExternalGroupId" })
        {
            Assert.Equal(256, binding.FindProperty(name)!.GetMaxLength());
            Assert.Equal("Latin1_General_100_BIN2", binding.FindProperty(name)!.GetCollation());
        }
        Assert.Equal(false, binding.FindProperty("IsEnabled")!.GetDefaultValue());
    }

    [Fact]
    public void RevisionAndOutboxUniquenessSupportOneCommittedGraphWithoutIdentityCursor()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var revision = model.FindEntityType(typeof(GroupMessageRevisionRecord))!;
        Assert.Contains(revision.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "CommittedSequence" }));
        Assert.Equal(ValueGenerated.Never, revision.FindProperty("CommittedSequence")!.ValueGenerated);
        var outbox = model.FindEntityType(typeof(GroupIngressOutboxRecord))!;
        Assert.Contains(outbox.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "MessageId", "Revision" }));
        Assert.Null(outbox.FindProperty("PayloadJson"));
        Assert.Equal(65536, revision.FindProperty("ProtectedContent")!.GetMaxLength());
        var state = model.FindEntityType(typeof(GroupSourceStateRecord))!;
        Assert.Contains(state.GetCheckConstraints(), x => x.Sql.Contains("[CommittedSequence] >= [ScheduledThroughSequence]", StringComparison.Ordinal));
        Assert.NotNull(state.FindProperty("FirstPendingAtUtc")); Assert.NotNull(state.FindProperty("LastPendingAtUtc"));
    }

    [Fact]
    public void MigrationIsAdditiveProtectsOperatorAuthorityAndAppendOnlyEvidenceAndRefusesDestructiveDown()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var migration = new AddGroupSourceIngress();
        var coverage = new AddGroupListenerOwnership();
        var inbox = new AddGroupIngressInbox();
        var allocation = new AddGroupBatchAllocation();
        var claims = new AddGroupBatchClaims();
        var expiry = new AddGroupBatchClaimExpiryFence();
        var notes = new AddGroupWorkNotes();
        var raw = new AddGroupWorkRawAccounting();
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations.Concat(coverage.UpOperations).Concat(inbox.UpOperations).Concat(allocation.UpOperations).Concat(claims.UpOperations).Concat(expiry.UpOperations).Concat(notes.UpOperations).Concat(raw.UpOperations).ToArray(), model).Select(x => x.CommandText));
        foreach (var table in new[] { "GroupConnectorAccounts", "GroupServices", "GroupBindings", "GroupServiceGrants", "GroupReaderGrants" })
        {
            Assert.Contains($"GRANT SELECT ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'INSERT')=0", GroupIngressPermissionVerifier.VerificationSql);
        }
        foreach (var table in new[] { "GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts", "GroupAccountCoverageGaps", "GroupListenerCommandReceipts", "GroupIngressInbox", "GroupBatchAllocations", "GroupBatchAllocatedRevisions", "GroupBatchClaimReceipts" })
        {
            Assert.Contains($"GRANT SELECT, INSERT ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'UPDATE')=0", GroupIngressPermissionVerifier.VerificationSql);
        }
        foreach (var entity in model.GetEntityTypes().Where(x => x.ClrType.Name.StartsWith("Group", StringComparison.Ordinal)))
        {
            Assert.Contains($"ALTER AUTHORIZATION ON OBJECT::[aioffice].[{entity.GetTableName()}] TO [aioffice_binding_operator_owner]", sql);
            Assert.Contains($"DENY UPDATE ON OBJECT::[aioffice].[{entity.GetTableName()}]", sql);
        }
        Assert.Contains("[ContentKeyId], [ProtectedContent], [SourceVersion], [DeletionGeneration]", sql);
        Assert.DoesNotContain("DROP TABLE", sql); Assert.DoesNotContain("ALTER TABLE [aioffice].[Tasks]", sql);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
        Assert.Throws<NotSupportedException>(() => coverage.DownOperations);
        Assert.Throws<NotSupportedException>(() => inbox.DownOperations);
        Assert.Throws<NotSupportedException>(() => allocation.DownOperations);
        Assert.Throws<NotSupportedException>(() => claims.DownOperations);
        Assert.Throws<NotSupportedException>(() => expiry.DownOperations);
        Assert.Throws<NotSupportedException>(() => raw.DownOperations);
        Assert.Contains("GRANT UPDATE ON OBJECT::[aioffice].[GroupBatchClaimStates] ([ExpiryObservedAtUtc])", sql);
        Assert.Contains("CHECK ([ExpiryObservedAtUtc] IS NULL OR ([ExpiryObservedAtUtc] >= [ExpiresAtUtc]", sql);
        Assert.Contains("HAS_PERMS_BY_NAME(N'aioffice.GroupBatchClaimStates',N'OBJECT',N'UPDATE',N'ExpiryObservedAtUtc',N'COLUMN')=1", GroupIngressPermissionVerifier.VerificationSql);
        Assert.Contains("GRANT UPDATE ON OBJECT::[aioffice].[GroupBatchClaimStates] ([Epoch], [OwnerId], [OperationId], [IssuedAtUtc], [ExpiresAtUtc])", sql);
        Assert.Contains("DENY UPDATE ON OBJECT::[aioffice].[GroupBatchClaimStates] ([TenantId], [CompanyId], [BindingId], [BatchId])", sql);
        Assert.Contains("HAS_PERMS_BY_NAME(N'aioffice.GroupBatchClaimStates',N'OBJECT',N'UPDATE',N'Epoch',N'COLUMN')=1", GroupIngressPermissionVerifier.VerificationSql);
    }

    [Fact]
    public void InboxIsAppendOnlyReferenceWithScopedOutboxRevisionServiceAndUniqueCommitSequence()
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupIngressInboxRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "EventId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        foreach (var name in new[] { "ProtectedContent", "Text", "PayloadJson", "UserId", "TaskId", "DestinationId", "SecretReference" })
            Assert.Null(entity.FindProperty(name));
        Assert.Contains(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupIngressOutboxRecord));
        Assert.Contains(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupMessageRevisionRecord));
        Assert.Contains(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupServiceRecord));
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "CommittedSequence" }));
        Assert.Contains(entity.GetCheckConstraints(), x => x.Sql.Contains("[CredentialEpoch] > 0 AND [GrantVersion] > 0", StringComparison.Ordinal));
    }

    [Fact]
    public void AccountCoverageIsScopedAppendOnlyMetadataAndCannotClaimReconnectedHistory()
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupAccountCoverageGapRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "ConnectorAccountId", "ListenerEpoch", "Reason" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal(32, entity.FindProperty("Reason")!.GetMaxLength());
        Assert.Equal("Latin1_General_100_BIN2", entity.FindProperty("Reason")!.GetCollation());
        Assert.Null(entity.FindProperty("ReconnectedAtUtc")); Assert.Null(entity.FindProperty("PayloadJson")); Assert.Null(entity.FindProperty("SourceText"));
        Assert.Contains(entity.GetCheckConstraints(), x => x.Sql.Contains("[RecordedAtUtc] >= [OpenedAtUtc]", StringComparison.Ordinal));
        Assert.Contains(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupConnectorAccountRecord));
    }
}
