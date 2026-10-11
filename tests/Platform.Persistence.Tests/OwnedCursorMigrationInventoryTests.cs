extern alias GroupReferenceProof;

using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;
using Sql = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupShadowCursorSql;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupSignedCursorMigrationRuntimeProof;
using Completion = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupCursorProofCompletion;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorMigrationInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotIncludesEveryTableCreatedByActualCompleteMigrationSql(bool shadow)
    {
        using var database = ColdDatabase();
        var created = MigrationTables(database, shadow ? Sql.PredecessorMigration : Sql.CursorMigration);
        var snapshots = shadow ? Sql.ShadowSnapshots(database, Sql.NewShadow()) : Sql.SourceSnapshots(database);
        try
        {
            Assert.Equal(58, created.Count); // 54 EF +three unmapped migration tables +history.
            Assert.All(created, x => Assert.Equal("aioffice", x.SchemaObjectName.SchemaIdentifier.Value));
            var expected = created.Select(x => x.SchemaObjectName.BaseIdentifier.Value).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, snapshots.Where(x => x.Tag.StartsWith("rows:", StringComparison.Ordinal))
                .Select(x => x.Tag[5..]).Order(StringComparer.Ordinal));
            var inventory = snapshots[0].Command.CommandText;
            var parsedInventory = new TSql160Parser(true).Parse(new StringReader(inventory), out var parseErrors);
            Assert.Empty(parseErrors);
            var values = new InventoryValues(); parsedInventory.Accept(values);
            Assert.Equal(2, values.Inventories.Count);
            Assert.All(values.Inventories, names => Assert.Equal(expected, names.Order(StringComparer.Ordinal)));
            Assert.Equal(2, Regex.Matches(inventory, @"\bEXCEPT\b").Count);
            Assert.Contains("WHERE t.is_ms_shipped=0", inventory);
            Assert.Equal(72, snapshots.Count);
            Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
            Assert.Empty(database.ChangeTracker.Entries());
        }
        finally { foreach (var item in snapshots) item.Command.Dispose(); }
    }

    [Theory]
    [InlineData("ToolExecutionAudit", 12, "AuditId")]
    [InlineData("CustomerAiCreditSettlements", 9, "SettlementId")]
    [InlineData("CustomerSlaPriorityAdmissions", 11, "AdmissionId")]
    public void UnmappedTableProjectionHasEveryActualMigrationColumnAndOriginalPrimaryKey(string name, int columnCount, string key)
    {
        using var database = ColdDatabase();
        var created = MigrationTables(database, Sql.PredecessorMigration).Single(x => x.SchemaObjectName.BaseIdentifier.Value == name);
        Assert.Equal(columnCount, created.Definition.ColumnDefinitions.Count);
        var primary = Assert.Single(created.Definition.TableConstraints.OfType<UniqueConstraintDefinition>(), x => x.IsPrimaryKey);
        Assert.Equal(key, Assert.Single(primary.Columns).Column.MultiPartIdentifier.Identifiers.Single().Value);
        foreach (var snapshots in new[] { Sql.SourceSnapshots(database), Sql.ShadowSnapshots(database, Sql.NewShadow()) })
        {
            try
            {
                var command = snapshots.Single(x => x.Tag == "rows:" + name).Command.CommandText;
                var columns = string.Join(",", created.Definition.ColumnDefinitions.Select(x => x.ColumnIdentifier.Value)
                    .Order(StringComparer.Ordinal).Select(x => "[" + x + "]"));
                Assert.Contains("SELECT " + columns + " FROM ", command);
                Assert.Contains("ORDER BY [" + key + "] FOR JSON PATH,INCLUDE_NULL_VALUES", command);
                Assert.Contains("COUNT_BIG(*)", command); Assert.Contains(">16384", command);
                Assert.Contains("DATALENGTH(@payload)>8388608", command);
                Assert.DoesNotContain("SELECT *", command); Assert.DoesNotContain(" TOP", command);
                var parser = new TSql160Parser(true); parser.Parse(new StringReader(command), out var errors); Assert.Empty(errors);
            }
            finally { foreach (var item in snapshots) item.Command.Dispose(); }
        }
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("old-model-only")]
    [InlineData("missing-raw")]
    [InlineData("duplicate")]
    [InlineData("wrong-first")]
    public async Task IncompleteOrMalformedSnapshotPlanRefusesBeforeConstructingConnection(string defect)
    {
        using var database = ColdDatabase();
        var original = Sql.SourceSnapshots(database);
        var changed = original.ToList();
        switch (defect)
        {
            case "old-model-only": changed.RemoveAll(x => x.Tag is "rows:ToolExecutionAudit" or "rows:CustomerAiCreditSettlements" or "rows:CustomerSlaPriorityAdmissions"); break;
            case "missing-raw": changed.RemoveAll(x => x.Tag == "rows:ToolExecutionAudit"); break;
            case "duplicate": changed[^1] = changed[1]; break;
            case "wrong-first": (changed[0], changed[1]) = (changed[1], changed[0]); break;
        }
        var completion = new Completion();
        try
        {
            var method = typeof(Proof).GetMethod("SnapshotAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var operation = Assert.IsAssignableFrom<Task<Dictionary<string, string>>>(method.Invoke(null,
                [completion, changed, "invalid connection string must never be parsed", CancellationToken.None]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            completion.RequireClean();
            Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        }
        finally { foreach (var item in original) item.Command.Dispose(); }
    }

    private static IReadOnlyList<CreateTableStatement> MigrationTables(PlatformDbContext database, string target)
    {
        // Parse real generated SQL, including SqlOperation CREATE and ALTER
        // ADD COLUMN bodies. The current EF model is not the physical inventory.
        var script = database.GetService<IMigrator>().GenerateScript(Migration.InitialDatabase, target,
            MigrationsSqlGenerationOptions.NoTransactions);
        var parsed = new TSql160Parser(true).Parse(new StringReader(script), out var errors);
        Assert.Empty(errors);
        var visitor = new CreatedTables(); parsed.Accept(visitor);
        Assert.Equal(visitor.Tables.Count, visitor.Tables.Select(x => x.SchemaObjectName.BaseIdentifier.Value).Distinct().Count());
        return visitor.Tables;
    }

    private static PlatformDbContext ColdDatabase() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_shadow_cold;Integrated Security=true;TrustServerCertificate=true",
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.DefaultSchema)).Options);

    private sealed class CreatedTables : TSqlFragmentVisitor
    {
        public List<CreateTableStatement> Tables { get; } = [];
        public override void ExplicitVisit(CreateTableStatement node) => Tables.Add(node);
        public override void ExplicitVisit(AlterTableAddTableElementStatement node)
        {
            // Ordered migration SQL: fold added columns into the original
            // definition before comparing complete raw row projections.
            var table = Tables.Single(x => x.SchemaObjectName.BaseIdentifier.Value == node.SchemaObjectName.BaseIdentifier.Value);
            foreach (var column in node.Definition.ColumnDefinitions) table.Definition.ColumnDefinitions.Add(column);
        }
    }

    private sealed class InventoryValues : TSqlFragmentVisitor
    {
        public List<string[]> Inventories { get; } = [];
        public override void ExplicitVisit(InlineDerivedTable node)
        {
            Assert.Equal("v", node.Alias.Value);
            Inventories.Add(node.RowValues.Select(row => Assert.IsType<StringLiteral>(Assert.Single(row.ColumnValues)).Value).ToArray());
        }
    }
}
