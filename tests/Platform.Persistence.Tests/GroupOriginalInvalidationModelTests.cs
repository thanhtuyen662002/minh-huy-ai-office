using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupOriginalInvalidationModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=unused.invalid;Database=offline_invalidation_schema;Integrated Security=true;Pooling=false").Options);

    [Fact]
    public void VersionOneDigestMatchesIndependentUtf8BinaryFramingGoldenVector()
    {
        // Expected SHA256 computed independently with Python UTF8 JSON and
        // 7-bit string length + little-endian Int32 framing; no candidate call.
        var row = new GroupOriginalInvalidationRecord
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            CompanyId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            BindingId = Guid.Parse("00000000-0000-0000-0000-000000000003"),
            BatchId = Guid.Parse("00000000-0000-0000-0000-000000000004"),
            FormatVersion = 1,
            PlanSha256 = new string('A', 64),
            OriginalClaimOperationId = Guid.Parse("00000000-0000-0000-0000-000000000005"),
            OriginalClaimEpoch = 2,
            ClaimOperationId = Guid.Parse("00000000-0000-0000-0000-000000000006"),
            ClaimEpoch = 3,
            ObservedDependenciesSha256 = new string('B', 64),
            CreatedAtUtc = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero)
        };
        Assert.Equal("22A3E6B2EB651E8AD8DD695D3C17771CF3DB5E0C847CB958E0B10A78972AE5BA", GroupOriginalInvalidationProof.Digest(row));
        row.CheckpointSha256 = new string('C', 64);
        Assert.Equal("22A3E6B2EB651E8AD8DD695D3C17771CF3DB5E0C847CB958E0B10A78972AE5BA", GroupOriginalInvalidationProof.Digest(row));
    }

    [Fact]
    public void CheckpointHasOneScopedKeyAndThreeExactImmutableParentsWithoutContentOrAuthority()
    {
        using var database = Database(); var model = database.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(GroupOriginalInvalidationRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(13, entity.GetProperties().Count());
        Assert.DoesNotContain(entity.GetProperties(), p => p.IsNullable || p.IsShadowProperty());
        Assert.Equal(3, entity.GetForeignKeys().Count());
        Assert.All(entity.GetForeignKeys(), foreign =>
        {
            Assert.Equal(DeleteBehavior.Restrict, foreign.DeleteBehavior);
            Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId" }, foreign.Properties.Take(3).Select(p => p.Name));
        });
        var root = Assert.Single(entity.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(GroupAutomaticInputPlanRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" }, root.Properties.Select(p => p.Name));
        Assert.Equal(root.Properties.Select(p => p.Name), root.PrincipalKey.Properties.Select(p => p.Name));
        var claims = entity.GetForeignKeys().Where(f => f.PrincipalEntityType.ClrType == typeof(GroupBatchClaimReceiptRecord)).ToArray();
        Assert.Equal(2, claims.Length);
        Assert.All(claims, c => Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" }, c.PrincipalKey.Properties.Select(p => p.Name)));
        Assert.Contains(claims, c => c.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "OriginalClaimOperationId", "BatchId", "OriginalClaimEpoch" }));
        Assert.Contains(claims, c => c.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "ClaimOperationId", "BatchId", "ClaimEpoch" }));
        Assert.DoesNotContain(entity.GetProperties(), p => p.Name is "Content" or "Plan" or "Generation" or "ReplacementPlan" or "TerminalManifest");
        Assert.Contains(model.FindEntityType(typeof(GroupOriginalAdmissionSidecarRecord))!.GetIndexes(), index =>
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" }));
    }

    [Theory]
    [InlineData("PlanSha256")]
    [InlineData("ObservedDependenciesSha256")]
    [InlineData("CheckpointSha256")]
    public void EveryDigestHasClosedUppercaseAsciiShape(string name)
    {
        using var database = Database(); var entity = database.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupOriginalInvalidationRecord))!;
        var property = entity.FindProperty(name)!;
        Assert.Equal(64, property.GetMaxLength()); Assert.True(property.IsFixedLength()); Assert.False(property.IsUnicode());
        Assert.Equal("Latin1_General_100_BIN2", property.GetCollation());
        var check = Assert.Single(entity.GetCheckConstraints(), c => c.Name == "CK_GroupOriginalInvalidations_Hashes");
        Assert.Contains($"DATALENGTH([{name}])=64", check.Sql);
        Assert.Contains($"[{name}] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2", check.Sql);
    }

    [Fact]
    public void MigrationOnlyAddsCheckpointTableAndIndexesAndRefusesDestructiveDown()
    {
        var migration = new AddGroupOriginalInvalidations(); var operations = migration.UpOperations;
        var table = Assert.Single(operations.OfType<CreateTableOperation>());
        Assert.Equal("GroupOriginalInvalidations", table.Name); Assert.Equal("aioffice", table.Schema); Assert.Equal(13, table.Columns.Count);
        Assert.Equal(3, table.ForeignKeys.Count); Assert.Equal(4, table.CheckConstraints.Count);
        Assert.Equal(3, operations.OfType<CreateIndexOperation>().Count());
        Assert.All(operations, operation => Assert.True(operation is CreateTableOperation or CreateIndexOperation or SqlOperation));
        Assert.All(table.ForeignKeys, foreign => Assert.Equal(ReferentialAction.Restrict, foreign.OnDelete));
        var sql = Assert.Single(operations.OfType<SqlOperation>()).Sql;
        Assert.Contains("ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupOriginalInvalidations] TO [aioffice_binding_operator_owner]", sql);
        Assert.Contains("GRANT SELECT, INSERT", sql); Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", sql);
        foreach (var column in table.Columns) Assert.Contains("[" + column.Name + "]", sql);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
        using var database = Database(); var model = database.GetService<IDesignTimeModel>().Model;
        foreach (var command in database.GetService<IMigrationsSqlGenerator>().Generate(operations, model))
        { new TSql160Parser(true).Parse(new StringReader(command.CommandText), out var errors); Assert.Empty(errors); }
        Assert.Equal(System.Data.ConnectionState.Closed, database.Database.GetDbConnection().State);
    }

    [Fact]
    public void PermissionProofIncludesOldAuthorityAndNewAppendOnlyAndTrustedGraphConstraints()
    {
        var sql = GroupOriginalInvalidationPermissionVerifier.EffectiveVerificationSql;
        foreach (var table in new[] { "GroupAutomaticInputPlans", "GroupOriginalInvalidations", "GroupServices", "GroupServiceGrants",
            "GroupWorkSourceDispositions", "GroupWorkRawDispositions", "GroupRequestEvidence", "GroupNotesCommittedItems" }) Assert.Contains(table, sql);
        foreach (var permission in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP", "COLUMN" })
            Assert.Contains(permission, GroupOriginalInvalidationPermissionVerifier.VerificationSql);
        Assert.Contains("f.is_disabled=1 OR f.is_not_trusted=1", sql);
        new TSql160Parser(false).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        // Original/accepted/terminal-FIRST proof contracts retain their prior text.
        Assert.DoesNotContain("GroupOriginalInvalidations", GroupAutomaticInputPlanPermissionVerifier.EffectiveVerificationSql);
        Assert.DoesNotContain("GroupOriginalInvalidations", GroupModelAttemptResultPermissionVerifier.EffectiveVerificationSql);
        Assert.DoesNotContain("GroupOriginalInvalidations", GroupTerminalAdvancePermissionVerifier.VerificationSql);
    }
}
