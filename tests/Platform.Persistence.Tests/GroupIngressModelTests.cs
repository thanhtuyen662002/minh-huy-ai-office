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
        Assert.Equal(12, entities.Length);
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
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model).Select(x => x.CommandText));
        foreach (var table in new[] { "GroupConnectorAccounts", "GroupServices", "GroupBindings", "GroupServiceGrants", "GroupReaderGrants" })
        {
            Assert.Contains($"GRANT SELECT ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'INSERT')=0", GroupIngressPermissionVerifier.VerificationSql);
        }
        foreach (var table in new[] { "GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts" })
        {
            Assert.Contains($"GRANT SELECT, INSERT ON OBJECT::[aioffice].[{table}]", sql);
            Assert.Contains($"DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table}]", sql);
        }
        foreach (var entity in model.GetEntityTypes().Where(x => x.ClrType.Name.StartsWith("Group", StringComparison.Ordinal)))
        {
            Assert.Contains($"ALTER AUTHORIZATION ON OBJECT::[aioffice].[{entity.GetTableName()}] TO [aioffice_binding_operator_owner]", sql);
            Assert.Contains($"DENY UPDATE ON OBJECT::[aioffice].[{entity.GetTableName()}]", sql);
        }
        Assert.Contains("[ContentKeyId], [ProtectedContent], [SourceVersion], [DeletionGeneration]", sql);
        Assert.DoesNotContain("DROP TABLE", sql); Assert.DoesNotContain("ALTER TABLE [aioffice].[Tasks]", sql);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }
}
