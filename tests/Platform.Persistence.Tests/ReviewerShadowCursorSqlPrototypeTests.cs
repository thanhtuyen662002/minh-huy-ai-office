extern alias GroupReferenceProof;

using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;
using Prototype = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupShadowCursorSql;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ReviewerShadowCursorSqlPrototypeTests
{
    internal static readonly string[] SeedTables = ["Companies", "GroupConnectorAccounts", "GroupServices", "GroupBindings",
        "GroupSourceStates", "GroupBatchAllocations", "GroupBatchClaimReceipts", "GroupBatchTerminalReceipts",
        "GroupTerminalAdvanceKeys", "GroupTerminalAdvances", "GroupTerminalFrontierStates"];

    internal static PlatformDbContext ColdDatabase() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_shadow_cold;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void RealRecursiveFkClosureIsExactlyElevenTablesIgnoringAdvanceSelfEdge()
    {
        using var db = ColdDatabase();
        var model = db.GetService<IDesignTimeModel>().Model;
        var byTable = model.GetEntityTypes().ToDictionary(x => x.GetTableName()!);
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(["GroupTerminalFrontierStates"]);
        while (queue.TryDequeue(out var table))
        {
            if (!closure.Add(table)) continue;
            foreach (var foreign in byTable[table].GetForeignKeys())
            {
                var parent = foreign.PrincipalEntityType.GetTableName()!;
                if (parent != table) queue.Enqueue(parent);
            }
        }
        Assert.Equal(SeedTables.Order(), closure.Order());
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    private static Prototype.RootIdentity Identity() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void RealModelSeedPlanHasElevenCompleteCopiesAndNoResourceOrConnection()
    {
        using var db = ColdDatabase(); var identity = Identity(); var shadow = Prototype.NewShadow();
        var plan = Prototype.BuildSeedPlan(db, shadow, identity);
        Assert.Equal(SeedTables, plan.Copies.Select(x => x.Tag[5..]));
        var tables = db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables;
        foreach (var copy in plan.Copies)
        {
            var sql = Parse(copy.Command.CommandText);
            var all = new Inserts(); sql.Accept(all);
            var insert = Assert.Single(all.Items).InsertSpecification;
            var target = Assert.IsType<NamedTableReference>(insert.Target).SchemaObject;
            Assert.Equal(shadow.Name, target.DatabaseIdentifier.Value); Assert.Equal("aioffice", target.SchemaIdentifier.Value);
            Assert.Equal(copy.Tag[5..], target.BaseIdentifier.Value);
            var columns = tables.Single(x => x.Name == target.BaseIdentifier.Value).Columns.Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(columns, insert.Columns.Select(x => Assert.Single(x.MultiPartIdentifier.Identifiers).Value));
            var select = Assert.IsType<SelectInsertSource>(insert.InsertSource).Select;
            var query = Assert.IsType<QuerySpecification>(select);
            Assert.Equal(columns, query.SelectElements.Select(x => Assert.Single(Assert.IsType<ColumnReferenceExpression>(Assert.IsType<SelectScalarExpression>(x).Expression).MultiPartIdentifier.Identifiers).Value));
            var source = Assert.IsType<NamedTableReference>(Assert.Single(query.FromClause.TableReferences));
            Assert.Equal("AIOfficeLocal", source.SchemaObject.DatabaseIdentifier.Value);
            Assert.Equal(target.BaseIdentifier.Value, source.SchemaObject.BaseIdentifier.Value);
            Assert.Equal("s", source.Alias.Value); Assert.NotNull(query.WhereClause);
            Assert.Contains("COUNT_BIG(*)", copy.Command.CommandText); Assert.Contains("@@ROWCOUNT<>1", copy.Command.CommandText);
            Assert.Contains("IF EXISTS(SELECT 1 FROM [" + shadow.Name + "]", copy.Command.CommandText);
            Assert.DoesNotContain("SELECT *", copy.Command.CommandText);
        }
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
        foreach (var template in plan.Copies.Prepend(plan.Guard))
        {
            Assert.Null(template.Command.Connection); Assert.Null(template.Command.Transaction);
            Assert.Equal(30, template.Command.CommandTimeout); Assert.Equal(5, template.Command.Parameters.Count);
            Assert.All(template.Command.Parameters.Cast<SqlParameter>(), x => Assert.Equal(SqlDbType.UniqueIdentifier, x.SqlDbType));
            Assert.Equal(new[] { identity.TenantId, identity.CompanyId, identity.SourceId, identity.ServiceId, identity.BatchId }, template.Command.Parameters.Cast<SqlParameter>().Select(x => (Guid)x.Value));
            Assert.DoesNotContain(identity.TenantId.ToString("D"), template.Command.CommandText);
        }
    }

    [Fact]
    public void EveryGuardCopyAndFullSourceShadowSnapshotParsesSql160WithoutConnection()
    {
        using var db = ColdDatabase(); var shadow = Prototype.NewShadow(); var plan = Prototype.BuildSeedPlan(db, shadow, Identity());
        var source = Prototype.SourceSnapshots(db); var target = Prototype.ShadowSnapshots(db, shadow);
        foreach (var item in plan.Copies.Prepend(plan.Guard).Concat(source).Concat(target)) Parse(item.Command.CommandText);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryEmptyTypedIdentityRefusesBeforeCommandConstruction(int index)
    {
        using var db = ColdDatabase(); var all = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        all[index] = Guid.Empty;
        var error = Assert.Throws<InvalidOperationException>(() => Prototype.BuildSeedPlan(db, Prototype.NewShadow(), new(all[0], all[1], all[2], all[3], all[4])));
        Assert.Equal("Owned shadow SQL template refused", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AIOfficeLocal")]
    [InlineData("GroupCursorShadow_0000000000000000000000000000000")]
    [InlineData("GroupCursorShadow_000000000000000000000000000000000")]
    [InlineData("GroupCursorShadow_ABCDEF00000000000000000000000000")]
    [InlineData("GroupCursorShadow_0000000000000000000000000000000g")]
    [InlineData("GroupCursorShadow_0000000000000000000000000000000]")]
    [InlineData("GroupCursorShadow_0000000000000000000000000000000;")]
    [InlineData("GroupCursorShadow_000000000000000000000000000000０")]
    [InlineData("GroupCursorShadow_00000000000000000000000000000000;DROP DATABASE AIOfficeLocal")]
    public void IdentifierInjectionAndCallerDatabaseNamesRefuse(string name)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Prototype.RequireShadowName(name));
        Assert.Equal("Owned shadow SQL template refused", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void ShadowNamesAreInternallyGeneratedClosedAndDistinct()
    {
        var names = Enumerable.Range(0, 32).Select(_ => Prototype.NewShadow().Name).ToArray();
        Assert.Equal(32, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, x => Assert.Matches("^GroupCursorShadow_[0-9a-f]{32}$", x));
        Assert.Empty(typeof(Prototype.ShadowDatabase).GetConstructors());
        Assert.All(names, x => Assert.Equal(x, Prototype.RequireShadowName(x)));
    }

    [Fact]
    public void EverySeedCommandRequiresOriginalCatalogOneSerializableUnitAndExactScopedExclusiveLock()
    {
        using var db = ColdDatabase(); var plan = Prototype.BuildSeedPlan(db, Prototype.NewShadow(), Identity());
        foreach (var command in plan.Copies.Prepend(plan.Guard))
        {
            var sql = command.Command.CommandText;
            Assert.Contains("DB_NAME()<>N'AIOfficeLocal'", sql); Assert.Contains("@@TRANCOUNT<>1", sql);
            Assert.Contains("XACT_STATE()<>1", sql); Assert.Contains("transaction_isolation_level=4", sql);
            Assert.Contains("N'aioffice:group-ingest:'", sql);
            foreach (var name in new[] { "@tenant", "@company", "@source" }) Assert.Contains("CONVERT(char(36)," + name + ")", sql);
            Assert.Contains("ISNULL(APPLOCK_MODE(N'public',@sourceResource,N'Transaction'),N'')<>N'Exclusive'", sql);
            Assert.DoesNotContain("sp_getapplock", sql); // Verifies caller ownership; never creates its own unit/authority.
        }
    }

    [Fact]
    public void SourceAccountOriginalClaimAndAdvanceKeySubqueriesAreIndependentlyFullyScoped()
    {
        using var db = ColdDatabase(); var plan = Prototype.BuildSeedPlan(db, Prototype.NewShadow(), Identity());
        foreach (var table in new[] { "GroupConnectorAccounts", "GroupBatchClaimReceipts", "GroupTerminalAdvanceKeys" })
        {
            var command = plan.Copies.Single(x => x.Tag == "copy:" + table).Command.CommandText;
            var visitor = new ScopedSubqueries(); Parse(command).Accept(visitor);
            Assert.NotEmpty(visitor.Items);
            foreach (var query in visitor.Items)
            {
                var name = Assert.IsType<NamedTableReference>(Assert.Single(query.FromClause.TableReferences));
                var alias = name.Alias.Value; var equalities = new Equalities(); query.WhereClause.Accept(equalities);
                var expected = name.SchemaObject.BaseIdentifier.Value switch
                {
                    "GroupBindings" => new[] { alias + ".TenantId=@tenant", alias + ".CompanyId=@company", alias + ".Id=@source" },
                    "GroupBatchTerminalReceipts" => new[] { alias + ".TenantId=@tenant", alias + ".CompanyId=@company", alias + ".BindingId=@source", alias + ".ServiceId=@service", alias + ".BatchId=@batch" },
                    "GroupTerminalAdvances" => new[] { alias + ".TenantId=@tenant", alias + ".CompanyId=@company", alias + ".BindingId=@source", alias + ".ServiceId=@service", alias + ".BatchId=@batch" },
                    _ => throw new InvalidOperationException("Unexpected scoped subquery")
                };
                Assert.All(expected, x => Assert.Contains(x, equalities.Items));
                Assert.Equal("AIOfficeLocal", name.SchemaObject.DatabaseIdentifier.Value);
                var column = Assert.Single(Assert.IsType<ColumnReferenceExpression>(Assert.IsType<SelectScalarExpression>(Assert.Single(query.SelectElements)).Expression).MultiPartIdentifier.Identifiers.Skip(1)).Value;
                Assert.Equal(table switch { "GroupConnectorAccounts" => "ConnectorAccountId", "GroupBatchClaimReceipts" => "ClaimOperationId", _ => "KeyId" }, column);
            }
        }
        Assert.DoesNotContain("GroupBatchClaimStates", string.Join("\n", plan.Copies.Select(x => x.Command.CommandText)));
    }

    [Fact]
    public void IndependentScopeDetectorRefusesDroppedCompanyClauseInOriginalClaimSubquery()
    {
        using var db = ColdDatabase(); var actual = Prototype.BuildSeedPlan(db, Prototype.NewShadow(), Identity()).Copies.Single(x => x.Tag == "copy:GroupBatchClaimReceipts").Command.CommandText;
        var original = new ScopedSubqueries(); Parse(actual).Accept(original);
        Assert.All(original.Items, query => { var e = new Equalities(); query.WhereClause.Accept(e); Assert.Contains("t.CompanyId=@company", e.Items); });
        var mutation = actual.Replace(" AND t.CompanyId=@company", "", StringComparison.Ordinal);
        var changed = new ScopedSubqueries(); Parse(mutation).Accept(changed);
        Assert.All(changed.Items, query => { var e = new Equalities(); query.WhereClause.Accept(e); Assert.DoesNotContain("t.CompanyId=@company", e.Items); });
    }

    [Fact]
    public void ExactSchemaChecksBothActualCatalogDirectionsAndModelWithoutDataRewrite()
    {
        using var db = ColdDatabase(); var shadow = Prototype.NewShadow(); var command = Prototype.BuildSeedPlan(db, shadow, Identity()).Guard.Command.CommandText;
        Assert.Equal(SeedTables.Length * 3 * 2, Regex.Matches(command, " EXCEPT ").Count);
        foreach (var name in SeedTables) Assert.Contains("t.name=N'" + name + "'", command);
        foreach (var attribute in new[] { "c.column_id", "ty.is_user_defined", "c.max_length", "c.precision", "c.scale", "c.collation_name", "c.is_identity", "c.is_computed", "c.is_sparse", "c.is_column_set", "c.generated_always_type", "c.encryption_type", "dc.definition", "cc.definition", "cc.is_persisted" }) Assert.Contains(attribute, command);
        Assert.Contains(Prototype.PredecessorMigration, command); Assert.Contains("MigrationId>", command);
        Assert.Contains("s.PreviousVersion IS NULL", command); Assert.Contains("c.UpdatedAtUtc=a.AdvancedAtUtc", command);
        Assert.Contains("t.ManifestSha256=a.TerminalManifestSha256", command); Assert.Contains("t.CommittedAtUtc=a.TerminalCommittedAtUtc", command);
        var dml = new Mutations(); Parse(command).Accept(dml); Assert.Empty(dml.Items);
        Assert.DoesNotContain("51048", command); // Helper refusal is never the actual migration51048 oracle.
    }

    [Fact]
    public void SnapshotInventoryCoversAllRealOwnedTablesAndFullCatalogHistoryWithOnlyHashResults()
    {
        using var db = ColdDatabase(); var snapshots = Prototype.SourceSnapshots(db);
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        Assert.Equal(model.Tables.Count() + 18, snapshots.Count); // inventory +3unmapped migration tables +history +13catalogs
        Assert.Equal(snapshots.Count, snapshots.Select(x => x.Tag).Distinct().Count());
        var rows = snapshots.Where(x => x.Tag.StartsWith("rows:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(model.Tables.Count() + 4, rows.Length);
        foreach (var table in model.Tables)
        {
            var row = rows.Single(x => x.Tag == "rows:" + table.Name).Command.CommandText;
            var order = string.Join(",", table.PrimaryKey!.Columns.Select(x => "[" + x.Name + "]"));
            Assert.Contains("ORDER BY " + order, row);
            Assert.All(table.Columns, column => Assert.Contains("[" + column.Name + "]", row));
            Assert.Contains("COUNT_BIG(*)", row); Assert.Contains(">16384", row); Assert.DoesNotContain(" TOP", row);
        }
        Assert.Contains("EXCEPT", snapshots[0].Command.CommandText);
        Assert.Contains("t.is_ms_shipped=0", snapshots[0].Command.CommandText);
        Assert.Contains("__EFMigrationsHistory", snapshots[0].Command.CommandText);
        foreach (var item in snapshots.Skip(1))
        {
            Assert.Contains("FOR JSON PATH,INCLUDE_NULL_VALUES", item.Command.CommandText);
            Assert.Contains("HASHBYTES('SHA2_256'", item.Command.CommandText);
            Assert.Contains("DATALENGTH(@payload)>8388608", item.Command.CommandText);
            Assert.Contains("AS [Sha256]", item.Command.CommandText);
            var dml = new Mutations(); Parse(item.Command.CommandText).Accept(dml); Assert.Empty(dml.Items);
            Assert.Null(item.Command.Connection); Assert.Null(item.Command.Transaction); Assert.Empty(item.Command.Parameters.Cast<SqlParameter>());
        }
        foreach (var tag in new[] { "objects", "columns", "checks", "foreignkeys", "indexes", "modules", "triggers", "permissions", "principals", "roles", "schemas", "properties", "database" }) Assert.Contains(snapshots, x => x.Tag == "catalog:" + tag);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void CatalogPropertiesIncludeDatabaseOwnershipAndAllObjectScopesWithoutVariantJson()
    {
        using var db = ColdDatabase(); var command = Prototype.ShadowSnapshots(db, Prototype.NewShadow()).Single(x => x.Tag == "catalog:properties").Command.CommandText;
        Assert.Contains(".sys.extended_properties ep", command);
        Assert.DoesNotContain("WHERE ep.", command); // class0 database ownership plus schema/table/index/column properties.
        Assert.Contains("ep.class,ep.class_desc,ep.major_id,ep.minor_id,ep.name", command);
        Assert.Contains("CONVERT(varbinary(8000),ep.value) AS [ValueBytes]", command);
        foreach (var property in new[] { "BaseType", "Precision", "Scale", "MaxLength", "Collation" }) Assert.Contains("SQL_VARIANT_PROPERTY(ep.value,'" + property + "')", command);
        Assert.DoesNotContain("ep.value AS", command); // sql_variant is not a supported direct FOR JSON value.
        Parse(command);
    }

    private static TSqlFragment Parse(string sql)
    {
        var result = new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.True(errors.Count == 0, string.Join(";", errors.Select(x => $"{x.Line}:{x.Column} {x.Message}")));
        return result;
    }

    private sealed class Inserts : TSqlFragmentVisitor
    {
        internal List<InsertStatement> Items { get; } = [];
        public override void ExplicitVisit(InsertStatement node) { Items.Add(node); base.ExplicitVisit(node); }
    }
    private sealed class ScopedSubqueries : TSqlFragmentVisitor
    {
        internal List<QuerySpecification> Items { get; } = [];
        public override void ExplicitVisit(ScalarSubquery node)
        {
            if (node.QueryExpression is QuerySpecification q && q.FromClause?.TableReferences.SingleOrDefault() is NamedTableReference t && t.Alias?.Value is "b" or "t" or "a") Items.Add(q);
            base.ExplicitVisit(node);
        }
    }
    private sealed class Equalities : TSqlFragmentVisitor
    {
        internal List<string> Items { get; } = [];
        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            if (node.ComparisonType == BooleanComparisonType.Equals && node.FirstExpression is ColumnReferenceExpression c && node.SecondExpression is VariableReference v)
                Items.Add(string.Join(".", c.MultiPartIdentifier.Identifiers.Select(x => x.Value)) + "=" + v.Name);
            base.ExplicitVisit(node);
        }
    }
    private sealed class Mutations : TSqlFragmentVisitor
    {
        internal List<TSqlStatement> Items { get; } = [];
        public override void ExplicitVisit(InsertStatement node) => Items.Add(node);
        public override void ExplicitVisit(UpdateStatement node) => Items.Add(node);
        public override void ExplicitVisit(DeleteStatement node) => Items.Add(node);
    }
}
