using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupTerminalAdvanceKeyModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
        "Server=127.0.0.1,1;Database=owned_key_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void ActualFiniteKeyRegistryAndAdvanceForeignKeyKeepExactScopeWithoutSecretBytes()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(GroupTerminalAdvanceKeyRecord))!;
        Assert.Equal(14, entity.GetProperties().Count()); Assert.All(entity.GetProperties(), p => Assert.False(p.IsNullable || p.IsShadowProperty()));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "KeyId" }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal("Latin1_General_100_BIN2", entity.FindProperty("KeyId")!.GetCollation());
        Assert.Equal(64, entity.FindProperty("KeyId")!.GetMaxLength()); Assert.False(entity.FindProperty("KeyId")!.IsUnicode());
        foreach (var name in new[] { "MaterialFingerprint", "AuthoritySha256" })
        { Assert.Equal("varbinary(32)", entity.FindProperty(name)!.GetColumnType()); Assert.Equal(32, entity.FindProperty(name)!.GetMaxLength()); }
        Assert.Equal(3, entity.GetForeignKeys().Count()); Assert.All(entity.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
        var fk = Assert.Single(model.FindEntityType(typeof(GroupTerminalAdvanceRecord))!.GetForeignKeys(), x => x.PrincipalEntityType == entity);
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "KeyId" }, fk.Properties.Select(p => p.Name)); Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.DoesNotContain(entity.GetProperties(), p => p.Name.Contains("Secret", StringComparison.Ordinal) || p.Name == "KeyMaterial");
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void VersionedExpansionRefusesUnenrolledLegacyBeforeDdlAndHasNoBlindBackfillOrDestructiveDown()
    {
        var migration = new AddGroupTerminalKeyEnrollment(); var operations = migration.UpOperations;
        var first = Assert.IsType<SqlOperation>(operations[0]); Assert.Contains("IF EXISTS", first.Sql); Assert.Contains("GroupTerminalAdvances", first.Sql); Assert.Contains("THROW 51047", first.Sql); Assert.False(first.SuppressTransaction);
        Assert.Equal("GroupTerminalAdvanceKeys", Assert.Single(operations.OfType<CreateTableOperation>()).Name);
        var fk = Assert.Single(operations.OfType<AddForeignKeyOperation>()); Assert.Equal("GroupTerminalAdvances", fk.Table); Assert.Equal("GroupTerminalAdvanceKeys", fk.PrincipalTable);
        Assert.DoesNotContain(operations, x => x is InsertDataOperation or UpdateDataOperation or DeleteDataOperation or AddColumnOperation or AlterColumnOperation or DropTableOperation);
        Assert.DoesNotContain(operations.OfType<SqlOperation>(), x => x.Sql.Contains("INSERT INTO", StringComparison.Ordinal) || x.Sql.Contains("UPDATE [", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Fact]
    public void FullIdempotentSqlAndCompleteEffectiveRightsRemainLegalColdMetadata()
    {
        using var db = Database(); var sql = db.GetService<IMigrator>().GenerateScript("20261011054423_AddGroupSignedTerminalAdvances", "20261011061648_AddGroupTerminalKeyEnrollment", MigrationsSqlGenerationOptions.Idempotent);
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        new TSql160Parser(true).Parse(new StringReader(GroupTerminalAdvanceKeyPermissionVerifier.VerificationSql), out errors); Assert.Empty(errors);
        var grants = new AddGroupTerminalKeyEnrollment().UpOperations.OfType<SqlOperation>().Last().Sql;
        foreach (var p in db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupTerminalAdvanceKeyRecord))!.GetProperties()) Assert.Contains("[" + p.Name + "]", grants);
        Assert.Contains("GRANT SELECT, INSERT", grants); Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", grants);
        Assert.Contains("sys.columns", GroupTerminalAdvanceKeyPermissionVerifier.VerificationSql); Assert.Contains("ISNULL(HAS_PERMS_BY_NAME", GroupTerminalAdvanceKeyPermissionVerifier.VerificationSql);
        foreach (var right in new[] { "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" }) Assert.Contains("N'" + right + "')=0", GroupTerminalAdvanceKeyPermissionVerifier.VerificationSql);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }
}
