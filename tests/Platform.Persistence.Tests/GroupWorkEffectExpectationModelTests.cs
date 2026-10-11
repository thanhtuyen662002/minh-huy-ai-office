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

public sealed class GroupWorkEffectExpectationModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_effect_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void ExpectationIsVersionedBoundedNullableAndKeepsLegacyDefaultsAndOriginalScopedKey()
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupWorkCommitReceiptRecord))!;
        Assert.True(entity.FindProperty("ExpectedEffectSha256")!.IsNullable);
        Assert.Equal(32, entity.FindProperty("ExpectedEffectSha256")!.GetMaxLength());
        Assert.Equal("varbinary(32)", entity.FindProperty("ExpectedEffectSha256")!.GetColumnType());
        Assert.False(entity.FindProperty("EffectLedgerVersion")!.IsNullable);
        Assert.Equal(0, entity.FindProperty("EffectLedgerVersion")!.GetDefaultValue());
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal("([EffectLedgerVersion]=0 AND [ExpectedEffectSha256] IS NULL) OR ([EffectLedgerVersion]=1 AND [DependencyManifestVersion]=1 AND [ExpectedEffectSha256] IS NOT NULL AND DATALENGTH([ExpectedEffectSha256])=32)",
            entity.GetCheckConstraints().Single(x => x.Name == "CK_GroupWorkCommitReceipts_Effects").Sql);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void MigrationExpandsOnlyTwoReceiptColumnsClosedCheckAndRuntimeImmutableDenial()
    {
        var operations = new AddGroupWorkEffectExpectation().UpOperations;
        var columns = operations.OfType<AddColumnOperation>().ToArray(); Assert.Equal(2, columns.Length);
        Assert.All(columns, c => { Assert.Equal("aioffice", c.Schema); Assert.Equal("GroupWorkCommitReceipts", c.Table); });
        Assert.Equal(new[] { "EffectLedgerVersion", "ExpectedEffectSha256" }, columns.Select(x => x.Name).Order());
        Assert.Equal(0, columns.Single(x => x.Name == "EffectLedgerVersion").DefaultValue);
        Assert.Equal("varbinary(32)", columns.Single(x => x.Name == "ExpectedEffectSha256").ColumnType);
        Assert.True(columns.Single(x => x.Name == "ExpectedEffectSha256").IsNullable);
        Assert.Single(operations.OfType<AddCheckConstraintOperation>());
        Assert.Equal("DENY UPDATE ON OBJECT::[aioffice].[GroupWorkCommitReceipts] ([EffectLedgerVersion], [ExpectedEffectSha256]) TO [aioffice_binding_runtime];",
            Assert.Single(operations.OfType<SqlOperation>()).Sql.Trim());
        Assert.Equal(4, operations.Count);
        Assert.Throws<NotSupportedException>(() => new AddGroupWorkEffectExpectation().DownOperations);
    }

    [Fact]
    public void ActualGeneratedForwardSqlParsesAndExistingColumnLevelPermissionProofIncludesFutureColumns()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var sql = string.Join('\n', db.GetService<IMigrationsSqlGenerator>()
            .Generate(new AddGroupWorkEffectExpectation().UpOperations, model).Select(x => x.CommandText));
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Contains("sys.columns c WHERE c.object_id=OBJECT_ID(N'aioffice.GroupWorkCommitReceipts')", GroupWorkNotePermissionVerifier.VerificationSql);
        Assert.Contains("UPDATE',c.name,N'COLUMN'),1)<>0", GroupWorkNotePermissionVerifier.VerificationSql);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
