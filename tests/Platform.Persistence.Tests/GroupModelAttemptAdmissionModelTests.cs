using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupModelAttemptAdmissionModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=offline_attempt_admission_schema;Integrated Security=true;Pooling=false").Options);

    [Fact]
    public void CompanionBindsExactScopedRequestBatchInputAndImmutableOriginalSidecarWithoutCascade()
    {
        using var database = Database(); var model = database.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(GroupModelAttemptAdmissionRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "RequestId" }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(15, entity.GetProperties().Count());
        Assert.DoesNotContain(entity.GetProperties(), p => p.IsNullable || p.IsShadowProperty());
        Assert.Equal(2, entity.GetForeignKeys().Count());
        var start = Assert.Single(entity.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(GroupModelAttemptStartRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "BatchId", "InputSha256" }, start.Properties.Select(p => p.Name));
        Assert.Equal(start.Properties.Select(p => p.Name), start.PrincipalKey.Properties.Select(p => p.Name));
        var sidecar = Assert.Single(entity.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(GroupOriginalAdmissionSidecarRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256", "SidecarSha256" }, sidecar.Properties.Select(p => p.Name));
        Assert.Equal(sidecar.Properties.Select(p => p.Name), sidecar.PrincipalKey.Properties.Select(p => p.Name));
        Assert.All(entity.GetForeignKeys(), f => Assert.Equal(DeleteBehavior.Restrict, f.DeleteBehavior));
        Assert.Equal("varbinary(max)", entity.FindProperty("Snapshot")!.GetColumnType());
        Assert.Equal(GroupStructuredAdmissionSnapshotCodec.MaximumBytes, entity.FindProperty("Snapshot")!.GetMaxLength());
        Assert.DoesNotContain(entity.GetProperties(), p => p.Name is "Input" or "Schema" or "Authorization" or "Endpoint" or "CurrentClaimEpoch");
        // Exact current/original claims remain authenticated through the
        // existing principal rows; adding duplicate free claim fields cannot
        // substitute for those immutable SQL identities.
        Assert.Contains(model.FindEntityType(typeof(GroupModelAttemptStartRecord))!.GetForeignKeys(),
            f => f.PrincipalEntityType.ClrType == typeof(GroupBatchClaimReceiptRecord));
        Assert.Contains(model.FindEntityType(typeof(GroupOriginalAdmissionSidecarRecord))!.GetForeignKeys(),
            f => f.PrincipalEntityType.ClrType == typeof(GroupBatchClaimReceiptRecord));
    }

    [Theory]
    [InlineData("InputSha256")]
    [InlineData("PlanSha256")]
    [InlineData("SidecarSha256")]
    [InlineData("SnapshotSha256")]
    public void EveryHashUsesExactUppercaseFixedAsciiAndBinaryCollation(string name)
    {
        using var database = Database(); var entity = database.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupModelAttemptAdmissionRecord))!;
        var property = entity.FindProperty(name)!;
        Assert.Equal(64, property.GetMaxLength()); Assert.True(property.IsFixedLength()); Assert.False(property.IsUnicode());
        Assert.Equal("Latin1_General_100_BIN2", property.GetCollation());
        var check = Assert.Single(entity.GetCheckConstraints(), c => c.Name == "CK_GroupModelAttemptAdmissions_Identity");
        Assert.Contains($"DATALENGTH([{name}])=64", check.Sql, StringComparison.Ordinal);
        Assert.Contains($"[{name}] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2", check.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void NewMigrationOnlyExpandsKeysAndTableAndRetainsEveryPriorIndex()
    {
        var migration = new AddGroupModelAttemptAdmissions(); var operations = migration.UpOperations;
        var table = Assert.Single(operations.OfType<CreateTableOperation>());
        Assert.Equal("GroupModelAttemptAdmissions", table.Name); Assert.Equal("aioffice", table.Schema); Assert.Equal(15, table.Columns.Count);
        Assert.Equal(2, operations.OfType<AddUniqueConstraintOperation>().Count());
        Assert.Equal(2, table.ForeignKeys.Count); Assert.All(table.ForeignKeys, f => Assert.Equal(ReferentialAction.Restrict, f.OnDelete));
        Assert.Equal(4, table.CheckConstraints.Count);
        Assert.DoesNotContain(operations, op => op is AlterColumnOperation or DropTableOperation or DropColumnOperation or DropIndexOperation
            or DropForeignKeyOperation or DropUniqueConstraintOperation or UpdateDataOperation or InsertDataOperation or DeleteDataOperation);
        var sql = string.Join("\n", operations.OfType<SqlOperation>().Select(op => op.Sql));
        Assert.Contains("ALTER AUTHORIZATION", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, INSERT", sql, StringComparison.Ordinal);
        Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", sql, StringComparison.Ordinal);
        foreach (var column in table.Columns) Assert.Contains("[" + column.Name + "]", sql, StringComparison.Ordinal);
        new TSql160Parser(false).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
        using var database = Database(); var model = database.GetService<IDesignTimeModel>().Model;
        Assert.Contains(model.FindEntityType(typeof(GroupOriginalAdmissionSidecarRecord))!.GetIndexes(), index =>
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" }));
    }

    [Fact]
    public void VersionChunkAndPreparationBoundsAreClosedAndGeneratedSqlParsesOffline()
    {
        using var database = Database(); var model = database.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(GroupModelAttemptAdmissionRecord))!;
        var checks = entity.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);
        Assert.Equal("[FormatVersion]=1 AND DATALENGTH([Snapshot]) BETWEEN 256 AND 10000", checks["CK_GroupModelAttemptAdmissions_Format"]);
        Assert.Contains("[ChunkOrdinal] BETWEEN 1 AND 100", checks["CK_GroupModelAttemptAdmissions_Chunk"], StringComparison.Ordinal);
        Assert.Contains("[ExpiresAtUtc]>[PreparedAtUtc]", checks["CK_GroupModelAttemptAdmissions_Time"], StringComparison.Ordinal);
        Assert.Contains("[ExpiresAtUtc]<=DATEADD(second,120,[PreparedAtUtc])", checks["CK_GroupModelAttemptAdmissions_Time"], StringComparison.Ordinal);
        var generated = database.GetService<IMigrationsSqlGenerator>().Generate(new AddGroupModelAttemptAdmissions().UpOperations, model);
        foreach (var command in generated)
        { new TSql160Parser(true).Parse(new StringReader(command.CommandText), out var errors); Assert.Empty(errors); }
        Assert.Equal(System.Data.ConnectionState.Closed, database.Database.GetDbConnection().State);
    }

    [Fact]
    public void NewPermissionProofIsExplicitAndAbsentFromLegacyJournalPlanAndAcceptedResultRecovery()
    {
        foreach (var text in new[] { GroupModelAttemptPermissionVerifier.EffectiveVerificationSql,
            GroupAutomaticInputPlanPermissionVerifier.EffectiveVerificationSql, GroupModelAttemptResultPermissionVerifier.EffectiveVerificationSql })
            Assert.DoesNotContain("GroupModelAttemptAdmissions", text, StringComparison.Ordinal);
        var sql = GroupModelAttemptAdmissionPermissionVerifier.EffectiveVerificationSql;
        foreach (var table in new[] { "GroupModelAttemptStarts", "GroupModelAttemptFailures", "GroupOriginalAdmissionSidecars", "GroupModelAttemptAdmissions" })
            Assert.Contains(table, sql, StringComparison.Ordinal);
        foreach (var permission in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP", "COLUMN" })
            Assert.Contains(permission, GroupModelAttemptAdmissionPermissionVerifier.VerificationSql, StringComparison.Ordinal);
        new TSql160Parser(false).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
    }
}
