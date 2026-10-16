extern alias GroupReferenceProof;

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;
using Sql = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupShadowCursorSql;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupSignedCursorMigrationRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorSnapshotEncodingTests
{
    // Independently computed SHA256 of UTF16LE JSON, not a call to the candidate serializer.
    private const string EmptyHash = "5EE9173B1B564E61AFD2359A897C180B5EA83875A91A28537DF4202AAD4B3AA7";
    private const string NullRowHash = "2C97B823D50ADDBA75158963C5368B8FEB50109D843D8255CF653AA8C1D6C8B4";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteActualSnapshotPlanCanonicalizesOnlyScalarEmptyJsonAndRetainsBounds(bool shadow)
    {
        using var database = ColdDatabase();
        var templates = shadow ? Sql.ShadowSnapshots(database, Sql.NewShadow()) : Sql.SourceSnapshots(database);
        try
        {
            Assert.Equal(shadow ? Sql.ShadowSnapshotCount : Sql.SourceSnapshotCount, templates.Count);
            Assert.Equal(shadow ? 58 : 65, templates.Count(x => x.Tag.StartsWith("rows:", StringComparison.Ordinal)));
            foreach (var template in templates)
            {
                RequireCanonicalSerializer(template.Command.CommandText);
                Assert.Null(template.Command.Connection); Assert.Null(template.Command.Transaction);
                Assert.Empty(template.Command.Parameters);
                if (template.Tag.StartsWith("rows:", StringComparison.Ordinal))
                    Assert.Contains(">16384", template.Command.CommandText);
            }
            Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
            Assert.Empty(database.ChangeTracker.Entries());
        }
        finally { foreach (var template in templates) template.Command.Dispose(); }
    }

    [Fact]
    public void FixedEngineControlsReadZeroRowsAndOneNullRowWithoutAccessingAnyTable()
    {
        var controls = Sql.SnapshotEncodingControls();
        try
        {
            Assert.Equal(new[] { "snapshot-control:empty", "snapshot-control:null-row" }, controls.Select(x => x.Tag));
            Assert.True(Assert.IsAssignableFrom<IList<Sql.CommandTemplate>>(controls).IsReadOnly);
            foreach (var (control, predicate) in controls.Zip(new[] { "WHERE 1=0", "WHERE 1=1" }))
            {
                var parsed = RequireCanonicalSerializer(control.Command.CommandText);
                var references = new TableReferences(); parsed.Accept(references);
                Assert.Equal(0, references.Count);
                Assert.Contains("SELECT CAST(NULL AS int) AS [Value] " + predicate, control.Command.CommandText);
                Assert.Null(control.Command.Connection); Assert.Null(control.Command.Transaction);
                Assert.Empty(control.Command.Parameters);
            }
        }
        finally { foreach (var control in controls) control.Command.Dispose(); }
    }

    [Theory]
    [InlineData("snapshot-control:empty", EmptyHash)]
    [InlineData("snapshot-control:null-row", NullRowHash)]
    public void EngineResultMustMatchIndependentExactUtf16JsonHash(string tag, string hash) =>
        Proof.RequireSnapshotEncodingControlResult(tag, hash);

    [Theory]
    [InlineData("snapshot-control:empty", NullRowHash)]
    [InlineData("snapshot-control:null-row", EmptyHash)]
    [InlineData("snapshot-control:unknown", EmptyHash)]
    [InlineData("rows:CustomerSlaPolicyRevisions", EmptyHash)]
    [InlineData("snapshot-control:empty", "5ee9173b1b564e61afd2359a897c180b5ea83875a91a28537df4202aad4b3aa7")]
    [InlineData("snapshot-control:empty", null)]
    public void EmptyNullRowForeignTagOrMalformedHashCannotSubstituteForEachOther(string tag, string? hash) =>
        Assert.Throws<InvalidOperationException>(() => Proof.RequireSnapshotEncodingControlResult(tag, hash!));

    [Fact]
    public void ExactCompiledOriginalPlanMapsHostedOrdinalFiveToSlaPolicyRows()
    {
        using var database = ColdDatabase();
        var templates = Sql.SourceSnapshots(database);
        try
        {
            Assert.Equal("inventory", templates[0].Tag);
            Assert.Equal("rows:CustomerSlaPolicyRevisions", templates[5].Tag);
        }
        finally { foreach (var template in templates) template.Command.Dispose(); }
    }

    private static TSqlFragment RequireCanonicalSerializer(string command)
    {
        var parsed = new TSql160Parser(true).Parse(new StringReader(command), out var errors);
        Assert.Empty(errors);
        var declarations = new PayloadDeclarations(); parsed.Accept(declarations);
        var payload = Assert.Single(declarations.Payloads);
        var coalesce = Assert.IsType<CoalesceExpression>(payload.Value);
        Assert.Equal(2, coalesce.Expressions.Count);
        Assert.IsType<ScalarSubquery>(coalesce.Expressions[0]);
        var fallback = Assert.IsType<StringLiteral>(coalesce.Expressions[1]);
        Assert.True(fallback.IsNational); Assert.Equal("[]", fallback.Value);
        Assert.Contains("FOR JSON PATH,INCLUDE_NULL_VALUES", command);
        Assert.Contains("@payload IS NULL OR DATALENGTH(@payload)>8388608", command);
        Assert.Contains("HASHBYTES('SHA2_256',CONVERT(varbinary(max),@payload))", command);
        Assert.DoesNotContain("WITHOUT_ARRAY_WRAPPER", command);
        return parsed;
    }

    private static PlatformDbContext ColdDatabase() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_shadow_cold;Integrated Security=true;TrustServerCertificate=true",
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.DefaultSchema)).Options);

    private sealed class PayloadDeclarations : TSqlFragmentVisitor
    {
        public List<DeclareVariableElement> Payloads { get; } = [];
        public override void ExplicitVisit(DeclareVariableElement node)
        {
            if (node.VariableName.Value == "@payload") Payloads.Add(node);
        }
    }

    private sealed class TableReferences : TSqlFragmentVisitor
    {
        public int Count { get; private set; }
        public override void ExplicitVisit(NamedTableReference node) => Count++;
    }
}
