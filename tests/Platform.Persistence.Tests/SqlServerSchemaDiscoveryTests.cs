using MinhHuy.AIOffice.Platform.Persistence;
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
