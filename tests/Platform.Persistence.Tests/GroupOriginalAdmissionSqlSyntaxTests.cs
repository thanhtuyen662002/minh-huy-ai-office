extern alias GroupReferenceProof;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalAdmissionRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupOriginalAdmissionSqlSyntaxTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualRuntimeInsertCommandsParseAndCoverEveryActualMappedColumn(bool sidecar)
    {
        var text = sidecar ? Proof.ConstraintAdmissionInsertSql : Proof.ConstraintOriginalInsertSql;
        var script = new TSql160Parser(true).Parse(new StringReader(text), out var errors);
        Assert.Empty(errors);
        var insert = Assert.IsType<InsertStatement>(Assert.Single(Assert.Single(Assert.IsType<TSqlScript>(script).Batches).Statements)).InsertSpecification;
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
            "Server=127.0.0.1,1;Database=offline_original_insert_syntax;Integrated Security=true;Pooling=false").Options);
        var entity = database.GetService<IDesignTimeModel>().Model.FindEntityType(sidecar
            ? typeof(GroupOriginalAdmissionSidecarRecord) : typeof(GroupAutomaticInputPlanRecord))!;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var target = Assert.IsType<NamedTableReference>(insert.Target).SchemaObject;
        Assert.Equal(table.Name, target.BaseIdentifier.Value); Assert.Equal(table.Schema, target.SchemaIdentifier.Value);
        var actualColumns = insert.Columns.Select(c => Assert.Single(c.MultiPartIdentifier.Identifiers).Value).ToArray();
        Assert.Equal(entity.GetProperties().Select(p => p.GetColumnName(table)).Order(), actualColumns.Order());
        Assert.Equal(actualColumns.Length, actualColumns.Distinct(StringComparer.Ordinal).Count());
        var values = Assert.Single(Assert.IsType<ValuesInsertSource>(insert.InsertSource).RowValues).ColumnValues;
        Assert.Equal(actualColumns.Length, values.Count);
        Assert.Equal(System.Data.ConnectionState.Closed, database.Database.GetDbConnection().State);
    }

    [Fact]
    public void ActualOriginalInsertDetectsTheUnquotedReservedPlanRegression()
    {
        Assert.Contains("[Plan]", Proof.ConstraintOriginalInsertSql, StringComparison.Ordinal);
        var broken = Proof.ConstraintOriginalInsertSql.Replace("[Plan]", "Plan", StringComparison.Ordinal);
        new TSql160Parser(true).Parse(new StringReader(broken), out var errors);
        Assert.Contains(errors, error => error.Number == 46010 && error.Message.Contains("Plan", StringComparison.Ordinal));
    }
}
