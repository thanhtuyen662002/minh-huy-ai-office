using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class PlatformDbContextTests
{
    [Fact]
    public void Model_UsesExpectedSchemaAndTable()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(
                "Server=localhost;Database=AIOffice_Model_Test;User Id=test;Password=test;TrustServerCertificate=true")
            .Options;

        using var context = new PlatformDbContext(options);

        var entity = context.Model.FindEntityType(typeof(PlatformMetadataRecord));

        Assert.NotNull(entity);
        Assert.Equal(PlatformDbContext.DefaultSchema, entity.GetSchema());
        Assert.Equal("PlatformMetadata", entity.GetTableName());
        Assert.Equal(200, entity.FindProperty(nameof(PlatformMetadataRecord.Key))?.GetMaxLength());
    }

    [Fact]
    public void ConnectionFactory_ReturnsClosedSqlConnection()
    {
        var factory = new SqlServerConnectionFactory();

        using var connection = factory.Create(
            "Server=localhost;Database=AIOffice_Model_Test;User Id=test;Password=test;TrustServerCertificate=true");

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }
}
