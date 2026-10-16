using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupWorkRawModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=InertRawModel;User ID=inert;Password=inert;TrustServerCertificate=true").Options);

    [Fact]
    public void RawLinksKeepEveryForeignKeyScopedRestrictiveAndMetadataOnly()
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupWorkRawDispositionRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "CommittedSequence" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal(4, entity.GetForeignKeys().Count());
        foreach (var foreign in entity.GetForeignKeys())
        {
            Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId" }, foreign.Properties.Take(3).Select(x => x.Name));
            Assert.Equal(DeleteBehavior.Restrict, foreign.DeleteBehavior);
            Assert.Contains(foreign.PrincipalEntityType.ClrType, new[] { typeof(GroupBatchAllocatedRevisionRecord),
                typeof(GroupWorkSourceDispositionRecord), typeof(GroupWorkCommitReceiptRecord), typeof(GroupMessageRevisionRecord) });
        }
        Assert.DoesNotContain(entity.GetProperties(), property => property.IsShadowProperty()
            || property.ClrType == typeof(string) || property.ClrType == typeof(byte[]));
        var check = Assert.Single(entity.GetCheckConstraints());
        Assert.Contains("[Relation]=1 AND [RawRevision]=[SelectedMessageRevision]", check.Sql);
        Assert.Contains("[Relation]=2 AND [RawRevision]<>[SelectedMessageRevision]", check.Sql);
    }

    [Fact]
    public void MigrationAddsOnlyRawLedgerWithExplicitImmutableOwnershipAndColumnRights()
    {
        var migration = new AddGroupWorkRawAccounting();
        Assert.Equal(6, migration.UpOperations.Count);
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("GroupWorkRawDispositions", table.Name); Assert.Equal("aioffice", table.Schema);
        Assert.Equal(11, table.Columns.Count); Assert.All(table.Columns, column => Assert.False(column.IsNullable));
        Assert.Equal(4, migration.UpOperations.OfType<CreateIndexOperation>().Count());
        var rights = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Contains("TO [aioffice_binding_operator_owner]", rights);
        Assert.Contains("GRANT SELECT, INSERT", rights); Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", rights);
        foreach (var column in table.Columns) Assert.Contains("[" + column.Name + "]", rights);
        Assert.DoesNotContain("GRANT UPDATE", rights); Assert.DoesNotContain("GRANT CONTROL", rights);
        Assert.Contains("OBJECT_ID(N'aioffice.GroupWorkRawDispositions',N'U') IS NOT NULL", GroupWorkNotePermissionVerifier.VerificationSql);
        Assert.Contains("HAS_PERMS_BY_NAME(N'aioffice.GroupWorkRawDispositions',N'OBJECT',N'UPDATE')=0", GroupWorkNotePermissionVerifier.VerificationSql);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Fact]
    public void ActualForwardMigrationAndRuntimeProofParseWithoutDatabaseOrOldTableMutation()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>()
            .Generate(new AddGroupWorkRawAccounting().UpOperations, model).Select(x => x.CommandText));
        new TSql160Parser(true).Parse(new StringReader(sql), out var migrationErrors); Assert.Empty(migrationErrors);
        new TSql160Parser(true).Parse(new StringReader(GroupWorkNotePermissionVerifier.VerificationSql), out var proofErrors); Assert.Empty(proofErrors);
        foreach (var forbidden in new[] { "DROP TABLE", "DROP COLUMN", "ALTER COLUMN", "INSERT INTO", "DELETE FROM", "UPDATE [" })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
