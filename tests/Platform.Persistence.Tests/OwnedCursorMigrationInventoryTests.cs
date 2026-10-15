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
        var created = MigrationTables(database, shadow ? Sql.PredecessorMigration : database.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).Last());
        var snapshots = shadow ? Sql.ShadowSnapshots(database, Sql.NewShadow()) : Sql.SourceSnapshots(database);
        try
        {
            Assert.Equal(shadow ? 58 : 64, created.Count); // Frozen54/current60 EF +three unmapped migration tables +history.
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
            Assert.Equal(shadow ? Sql.ShadowSnapshotCount : Sql.SourceSnapshotCount, snapshots.Count);
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
                var parser = new TSql160Parser(true); var parsed = parser.Parse(new StringReader(command), out var errors); Assert.Empty(errors);
                var shapes = new ShapeValues(); parsed.Accept(shapes);
                var expectedShape = created.Definition.ColumnDefinitions.Select(column =>
                    (column.ColumnIdentifier.Value, StoreType(column.DataType),
                        Assert.Single(column.Constraints.OfType<NullableConstraintDefinition>()).Nullable, false))
                    .OrderBy(x => x.Value, StringComparer.Ordinal).ToArray();
                Assert.Equal(2, shapes.Columns.Count);
                Assert.All(shapes.Columns, actual => Assert.Equal(expectedShape, actual));
                Assert.Equal(2, shapes.Keys.Count);
                Assert.All(shapes.Keys, actual => Assert.Equal(new[] { (key, 1) }, actual));
                Assert.Equal(4, Regex.Matches(command, @"\bEXCEPT\b").Count);
                Assert.Contains("c.is_nullable,ty.is_user_defined", command);
                Assert.Contains("i.is_primary_key=1 AND ic.key_ordinal>0", command);
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
                [completion, changed, Sql.SourceSnapshotCount, "invalid connection string must never be parsed", CancellationToken.None]));
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
        public override void ExplicitVisit(AlterTableAlterColumnStatement node)
        {
            var table = Tables.Single(x => x.SchemaObjectName.BaseIdentifier.Value == node.SchemaObjectName.BaseIdentifier.Value);
            var column = table.Definition.ColumnDefinitions.Single(x => x.ColumnIdentifier.Value == node.ColumnIdentifier.Value);
            if (node.DataType is not null) column.DataType = node.DataType;
            if (node.AlterTableAlterColumnOption is AlterTableAlterColumnOption.Null or AlterTableAlterColumnOption.NotNull)
            {
                foreach (var old in column.Constraints.OfType<NullableConstraintDefinition>().ToArray()) column.Constraints.Remove(old);
                column.Constraints.Add(new NullableConstraintDefinition { Nullable = node.AlterTableAlterColumnOption == AlterTableAlterColumnOption.Null });
            }
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

    private static string StoreType(DataTypeReference reference)
    {
        var type = Assert.IsType<SqlDataTypeReference>(reference);
        var name = type.SqlDataTypeOption.ToString().ToLowerInvariant();
        var parameters = type.Parameters.Select(x => x.Value).ToArray();
        if (parameters.Length == 0 && name is "datetimeoffset" or "datetime2" or "time") parameters = ["7"];
        return name + (parameters.Length == 0 ? "" : "(" + string.Join(",", parameters) + ")");
    }

    private sealed class ShapeValues : TSqlFragmentVisitor
    {
        public List<(string Name, string StoreType, bool Nullable, bool UserDefined)[]> Columns { get; } = [];
        public List<(string Name, int Ordinal)[]> Keys { get; } = [];
        public override void ExplicitVisit(InlineDerivedTable node)
        {
            Assert.Equal("v", node.Alias.Value);
            if (node.Columns.Count == 4)
            {
                Assert.Equal(new[] { "Name", "StoreType", "Nullable", "UserDefined" }, node.Columns.Select(x => x.Value));
                Columns.Add(node.RowValues.Select(row =>
                    (Assert.IsType<StringLiteral>(row.ColumnValues[0]).Value, Assert.IsType<StringLiteral>(row.ColumnValues[1]).Value,
                        Bit(row.ColumnValues[2]), Bit(row.ColumnValues[3]))).ToArray());
            }
            else
            {
                Assert.Equal(new[] { "Name", "Ordinal" }, node.Columns.Select(x => x.Value));
                Keys.Add(node.RowValues.Select(row => (Assert.IsType<StringLiteral>(row.ColumnValues[0]).Value,
                    int.Parse(Assert.IsType<IntegerLiteral>(row.ColumnValues[1]).Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray());
            }
        }

        private static bool Bit(ScalarExpression expression)
        {
            var cast = Assert.IsType<CastCall>(expression);
            Assert.Equal(SqlDataTypeOption.Bit, Assert.IsType<SqlDataTypeReference>(cast.DataType).SqlDataTypeOption);
            var value = Assert.IsType<IntegerLiteral>(cast.Parameter).Value; Assert.Contains(value, new[] { "0", "1" });
            return value == "1";
        }
    }
}
