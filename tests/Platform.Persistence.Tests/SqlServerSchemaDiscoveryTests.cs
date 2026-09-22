using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class SqlServerSchemaDiscoveryTests
{
    [Theory]
    [InlineData(" tenant-1", "company-1", "source-1")]
    [InlineData("tenant-1", "company-1 ", "source-1")]
    [InlineData("tenant-1", "company-1", " source-1")]
    public async Task DiscoverAsync_RejectsNonCanonicalAuthorityBeforeOpeningConnection(string tenantId, string companyId, string dataSourceId)
    {
        var factory = new RejectingConnectionFactory();
        var discovery = new SqlServerSchemaDiscovery(factory);
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await discovery.DiscoverAsync(tenantId, companyId, dataSourceId, 1, "Server=unused;Database=unused"));
        Assert.False(factory.WasCalled);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsInvalidVersionBeforeOpeningConnection()
    {
        var factory = new RejectingConnectionFactory();
        var discovery = new SqlServerSchemaDiscovery(factory);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await discovery.DiscoverAsync("tenant-1", "company-1", "source-1", 0, "Server=unused;Database=unused"));
        Assert.False(factory.WasCalled);
    }

    [Fact]
    public void DiscoverySql_UsesReadOnlyCatalogProjectionWithoutProvisionedCustomerObjects()
    {
        var sql = SqlServerSchemaDiscovery.DiscoverySql;
        Assert.Contains("sys.objects", sql, StringComparison.Ordinal);
        Assert.Contains("sys.columns", sql, StringComparison.Ordinal);
        Assert.Contains("sys.foreign_keys", sql, StringComparison.Ordinal);
        Assert.Contains("sys.foreign_key_columns", sql, StringComparison.Ordinal);
        Assert.Contains("sys.indexes", sql, StringComparison.Ordinal);
        Assert.Contains("sys.index_columns", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY object_kind, schema_name, object_name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AIOfficeSchemaDiscoveryView", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverySql_FingerprintsStructuralDetailsForTablesForeignKeysAndIndexes()
    {
        var sql = SqlServerSchemaDiscovery.DiscoverySql;
        Assert.Contains("c.is_nullable", sql, StringComparison.Ordinal);
        Assert.Contains("c.is_identity", sql, StringComparison.Ordinal);
        Assert.Contains("c.is_computed", sql, StringComparison.Ordinal);
        Assert.Contains("fkc.parent_column_id", sql, StringComparison.Ordinal);
        Assert.Contains("fkc.referenced_column_id", sql, StringComparison.Ordinal);
        Assert.Contains("i.is_unique", sql, StringComparison.Ordinal);
        Assert.Contains("ic.is_descending_key", sql, StringComparison.Ordinal);
        Assert.Contains("ic.is_included_column", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_PreservesAuthorityAndCanonicalizesObjectOrder()
    {
        var authority = new ErpSchemaSnapshot("tenant-1", "company-1", "source-1", 4, []);
        var snapshot = SqlServerSchemaDiscovery.Materialize(authority,
        [
            ("VIEW", "dbo", "ZView", "CREATE VIEW dbo.ZView AS SELECT 1 AS Value"),
            ("TABLE", "sales", "Orders", "1:Id:int:4:10:0:nullable=0")
        ]);
        Assert.Equal("tenant-1", snapshot.TenantId);
        Assert.Equal("company-1", snapshot.CompanyId);
        Assert.Equal("source-1", snapshot.DataSourceId);
        Assert.Equal(4, snapshot.Version);
        Assert.Equal([ErpSchemaObjectKind.Table, ErpSchemaObjectKind.View], snapshot.Objects.Select(item => item.Kind));
        Assert.All(snapshot.Objects, item => Assert.Equal(64, item.DefinitionHash.Length));
    }

    [Theory]
    [InlineData(" dbo", "Orders")]
    [InlineData("dbo", "Orders ")]
    [InlineData("", "Orders")]
    public void Materialize_FailsClosedForNonCanonicalMetadata(string schema, string name)
    {
        var authority = new ErpSchemaSnapshot("tenant-1", "company-1", "source-1", 1, []);
        Assert.Throws<InvalidOperationException>(() => SqlServerSchemaDiscovery.Materialize(authority, [("TABLE", schema, name, "definition")]));
    }

    [Theory]
    [InlineData("TABLE", ErpSchemaObjectKind.Table)]
    [InlineData("VIEW", ErpSchemaObjectKind.View)]
    [InlineData("PROCEDURE", ErpSchemaObjectKind.StoredProcedure)]
    [InlineData("FUNCTION", ErpSchemaObjectKind.Function)]
    [InlineData("TRIGGER", ErpSchemaObjectKind.Trigger)]
    [InlineData("FOREIGN_KEY", ErpSchemaObjectKind.ForeignKey)]
    [InlineData("INDEX", ErpSchemaObjectKind.Index)]
    public void ParseKind_MapsOnlySupportedProjectionKinds(string value, ErpSchemaObjectKind expected) => Assert.Equal(expected, SqlServerSchemaDiscovery.ParseKind(value));

    [Fact]
    public void ParseKind_FailsClosedForUnknownProjectionKind() => Assert.Throws<InvalidOperationException>(() => SqlServerSchemaDiscovery.ParseKind("SEQUENCE"));

    [Fact]
    public void HashDefinition_IsDeterministicAndDoesNotExposeDefinitionText()
    {
        const string definition = "CREATE VIEW dbo.SecretView AS SELECT SecretColumn FROM dbo.SecretTable";
        var first = SqlServerSchemaDiscovery.HashDefinition(definition);
        var second = SqlServerSchemaDiscovery.HashDefinition(definition);
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.DoesNotContain("Secret", first, StringComparison.Ordinal);
    }

    private sealed class RejectingConnectionFactory : ISqlConnectionFactory
    {
        public bool WasCalled { get; private set; }
        public System.Data.Common.DbConnection Create(string connectionString)
        {
            WasCalled = true;
            throw new InvalidOperationException("Connection creation must not occur for invalid authority.");
        }
    }
}
