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
    public async Task DiscoverAsync_RejectsNonCanonicalAuthorityBeforeOpeningConnection(
        string tenantId,
        string companyId,
        string dataSourceId)
    {
        var factory = new RejectingConnectionFactory();
        var discovery = new SqlServerSchemaDiscovery(factory);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await discovery.DiscoverAsync(
                tenantId,
                companyId,
                dataSourceId,
                1,
                "Server=unused;Database=unused"));

        Assert.False(factory.WasCalled);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsInvalidVersionBeforeOpeningConnection()
    {
        var factory = new RejectingConnectionFactory();
        var discovery = new SqlServerSchemaDiscovery(factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await discovery.DiscoverAsync(
                "tenant-1",
                "company-1",
                "source-1",
                0,
                "Server=unused;Database=unused"));

        Assert.False(factory.WasCalled);
    }

    [Fact]
    public void DiscoverySql_UsesNarrowDeterministicProjection()
    {
        var sql = SqlServerSchemaDiscovery.DiscoverySql;

        Assert.Contains("SELECT object_kind, schema_name, object_name, definition_text", sql, StringComparison.Ordinal);
        Assert.Contains("FROM dbo.AIOfficeSchemaDiscoveryView", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY object_kind, schema_name, object_name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("TABLE", ErpSchemaObjectKind.Table)]
    [InlineData("VIEW", ErpSchemaObjectKind.View)]
    [InlineData("PROCEDURE", ErpSchemaObjectKind.StoredProcedure)]
    [InlineData("FUNCTION", ErpSchemaObjectKind.Function)]
    [InlineData("TRIGGER", ErpSchemaObjectKind.Trigger)]
    [InlineData("FOREIGN_KEY", ErpSchemaObjectKind.ForeignKey)]
    [InlineData("INDEX", ErpSchemaObjectKind.Index)]
    public void ParseKind_MapsOnlySupportedProjectionKinds(string value, ErpSchemaObjectKind expected)
    {
        Assert.Equal(expected, SqlServerSchemaDiscovery.ParseKind(value));
    }

    [Fact]
    public void ParseKind_FailsClosedForUnknownProjectionKind()
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerSchemaDiscovery.ParseKind("SEQUENCE"));
    }

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
