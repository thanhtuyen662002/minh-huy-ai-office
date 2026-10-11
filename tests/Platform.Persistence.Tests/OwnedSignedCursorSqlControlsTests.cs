extern alias GroupReferenceProof;

using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupSignedCursorRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedSignedCursorSqlControlsTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
        "Server=127.0.0.1,1;Database=owned_cursor_cold;Integrated Security=true;TrustServerCertificate=true").Options);
    private static GroupScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void ActualLockCommandBindsOnlyTheValidatedScopedTransactionResource()
    {
        var scope = Scope(); var sql = Proof.SourceLockSql(scope);
        Assert.Equal($"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}", Assert.Single(sql.GetArguments()));
        var rendered = sql.Format.Replace("{0}", "@p0", StringComparison.Ordinal);
        new TSql160Parser(true).Parse(new StringReader(rendered), out var errors); Assert.Empty(errors);
        Assert.Contains("@LockMode=N'Exclusive'", rendered); Assert.Contains("@LockOwner=N'Transaction'", rendered);
        Assert.Contains("@LockTimeout=5000", rendered); Assert.Contains("SELECT @rc AS [Value]", rendered);
        Assert.DoesNotContain(scope.TenantId.ToString("D"), rendered);
    }

    [Fact]
    public void ActualUncomposedCatalogSqlParsesAndEveryOrdinalMatchesRealEfForeignKey()
    {
        using var db = Database();
        var rendered = db.Database.SqlQueryRaw<int>(Proof.CatalogSql).ToQueryString();
        new TSql160Parser(true).Parse(new StringReader(rendered), out var errors); Assert.Empty(errors);
        var model = db.GetService<IDesignTimeModel>().Model;
        var cursor = model.FindEntityType(typeof(GroupTerminalFrontierStateRecord))!;
        var fk = Assert.Single(cursor.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupTerminalAdvanceRecord));
        var childTable = StoreObjectIdentifier.Table("GroupTerminalFrontierStates", "aioffice");
        var parentTable = StoreObjectIdentifier.Table("GroupTerminalAdvances", "aioffice");
        Assert.Equal(7, fk.Properties.Count);
        for (var index = 0; index < fk.Properties.Count; index++)
            Assert.Contains($"({index + 1},N'{fk.Properties[index].GetColumnName(childTable)}',N'{fk.PrincipalKey.Properties[index].GetColumnName(parentTable)}')", Proof.CatalogSql);
        foreach (var clause in new[] { "fk.is_disabled=0", "fk.is_not_trusted=0", "fk.delete_referential_action=0",
            "c.is_disabled=0", "c.is_not_trusted=0", "constraint_column_id", "CK_GroupTerminalFrontierStates_Signed" }) Assert.Contains(clause, rendered);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void AllEightActualProbesAreSingleUpdatesWithOnlyThreeTypedScopeArguments()
    {
        var scope = Scope(); var statements = Proof.Denials(scope).Select(x => x.Sql).Concat(Proof.Noops(scope)).ToArray();
        Assert.Equal(8, statements.Length);
        foreach (var sql in statements)
        {
            Assert.Equal(new object[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId }, sql.GetArguments());
            var rendered = Regex.Replace(sql.Format, @"\{([0-9]+)\}", x => "@p" + x.Groups[1].Value);
            var script = Assert.IsType<TSqlScript>(new TSql160Parser(true).Parse(new StringReader(rendered), out var errors));
            Assert.Empty(errors);
            var update = Assert.IsType<UpdateStatement>(Assert.Single(Assert.Single(script.Batches).Statements)).UpdateSpecification;
            var table = Assert.IsType<NamedTableReference>(update.Target).SchemaObject;
            Assert.Equal("aioffice", table.SchemaIdentifier.Value); Assert.Equal("GroupTerminalFrontierStates", table.BaseIdentifier.Value);
            Assert.NotNull(update.WhereClause); var visitor = new ScopeVisitor(); update.WhereClause.Accept(visitor);
            Assert.Equal(new[] { "TenantId=@p0", "CompanyId=@p1", "BindingId=@p2" }, visitor.Equalities);
            Assert.All(update.SetClauses, x => Assert.IsType<AssignmentSetClause>(x));
        }
    }

    [Fact]
    public void DenialsNameExactNewConstraintsAndPositiveProbesCoverAllFourPermittedColumns()
    {
        var scope = Scope(); var denials = Proof.Denials(scope);
        Assert.Equal(4, denials.Length);
        Assert.All(denials.Take(3), x => Assert.Equal("FK_GroupTerminalFrontierStates_SignedAdvance", x.Constraint));
        Assert.Equal("CK_GroupTerminalFrontierStates_Signed", denials[3].Constraint);
        Assert.Contains("[ThroughSequence]=0,[LastTerminalBatchId]=NULL", denials[3].Sql.Format);
        var columns = new List<string>();
        foreach (var sql in Proof.Noops(scope))
        {
            var rendered = Regex.Replace(sql.Format, @"\{([0-9]+)\}", x => "@p" + x.Groups[1].Value);
            var script = Assert.IsType<TSqlScript>(new TSql160Parser(true).Parse(new StringReader(rendered), out var errors)); Assert.Empty(errors);
            var update = Assert.IsType<UpdateStatement>(Assert.Single(Assert.Single(script.Batches).Statements)).UpdateSpecification;
            var assignment = Assert.IsType<AssignmentSetClause>(Assert.Single(update.SetClauses));
            var field = Assert.Single(assignment.Column.MultiPartIdentifier.Identifiers).Value;
            Assert.Equal(field, Assert.Single(Assert.IsType<ColumnReferenceExpression>(assignment.NewValue).MultiPartIdentifier.Identifiers).Value);
            columns.Add(field);
        }
        Assert.Equal(new[] { "Version", "ThroughSequence", "UpdatedAtUtc", "LastTerminalBatchId" }, columns);
    }

    private sealed class ScopeVisitor : TSqlFragmentVisitor
    {
        internal List<string> Equalities { get; } = [];
        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            Assert.Equal(BooleanComparisonType.Equals, node.ComparisonType);
            var field = Assert.Single(Assert.IsType<ColumnReferenceExpression>(node.FirstExpression).MultiPartIdentifier.Identifiers).Value;
            Equalities.Add(field + "=" + Assert.IsType<VariableReference>(node.SecondExpression).Name);
        }
    }
}
