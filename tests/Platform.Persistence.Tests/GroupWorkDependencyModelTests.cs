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

public sealed class GroupWorkDependencyModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_manifest_schema;User ID=inert;Password=inert;TrustServerCertificate=true").Options);

    [Fact]
    public void ReceiptExpansionIsNullableVersionedBoundedAndKeepsLegacyDefaultAndEveryOriginalScopedKey()
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupWorkCommitReceiptRecord))!;
        Assert.True(entity.FindProperty("DependencyManifest")!.IsNullable);
        Assert.Equal(6902, entity.FindProperty("DependencyManifest")!.GetMaxLength());
        Assert.False(entity.FindProperty("DependencyManifestVersion")!.IsNullable);
        Assert.Equal(0, entity.FindProperty("DependencyManifestVersion")!.GetDefaultValue());
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        var constraint = entity.GetCheckConstraints().Single(x => x.Name == "CK_GroupWorkCommitReceipts_Dependencies").Sql;
        Assert.Contains("[DependencyManifestVersion]=0 AND [DependencyManifest] IS NULL", constraint);
        Assert.Contains("[DependencyManifestVersion]=1 AND [DependencyManifest] IS NOT NULL", constraint);
        Assert.Contains("BETWEEN 218 AND 6902", constraint);
        foreach (var name in new[] { "Text", "Title", "Body", "PlaintextSha256", "ApiKey", "Authorization", "ProviderEndpoint" }) Assert.Null(entity.FindProperty(name));
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void MigrationAddsOnlyTwoReceiptColumnsClosedCheckAndExplicitImmutableColumnDenial()
    {
        var operations = new AddGroupWorkDependencyManifest().UpOperations;
        var columns = operations.OfType<AddColumnOperation>().ToArray(); Assert.Equal(2, columns.Length);
        Assert.All(columns, column => { Assert.Equal("aioffice", column.Schema); Assert.Equal("GroupWorkCommitReceipts", column.Table); });
        Assert.Equal(new[] { "DependencyManifest", "DependencyManifestVersion" }, columns.Select(x => x.Name).Order());
        Assert.Equal("varbinary(6902)", columns.Single(x => x.Name == "DependencyManifest").ColumnType);
        Assert.Equal(0, columns.Single(x => x.Name == "DependencyManifestVersion").DefaultValue);
        Assert.Single(operations.OfType<AddCheckConstraintOperation>());
        var sql = Assert.Single(operations.OfType<SqlOperation>()).Sql;
        Assert.Contains("DENY UPDATE ON OBJECT::[aioffice].[GroupWorkCommitReceipts] ([DependencyManifestVersion], [DependencyManifest])", sql);
        Assert.Contains("TO [aioffice_binding_runtime]", sql);
        Assert.Equal(4, operations.Count);
        Assert.Throws<NotSupportedException>(() => new AddGroupWorkDependencyManifest().DownOperations);
    }

    [Fact]
    public void ActualGeneratedMigrationAndEffectivePermissionProofParseWithoutSqlConnection()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var sql = string.Join('\n', db.GetService<IMigrationsSqlGenerator>()
            .Generate(new AddGroupWorkDependencyManifest().UpOperations, model).Select(x => x.CommandText));
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        new TSql160Parser(true).Parse(new StringReader(GroupWorkNotePermissionVerifier.VerificationSql), out var proofErrors); Assert.Empty(proofErrors);
        Assert.Contains("sys.columns c WHERE c.object_id=OBJECT_ID(N'aioffice.GroupWorkCommitReceipts')", GroupWorkNotePermissionVerifier.VerificationSql);
        Assert.Contains("UPDATE',c.name,N'COLUMN'),1)<>0", GroupWorkNotePermissionVerifier.VerificationSql);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
