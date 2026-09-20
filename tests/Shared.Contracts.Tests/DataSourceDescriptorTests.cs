using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class DataSourceDescriptorTests
{
    [Fact]
    public void Create_ExposesRoutingPolicyWithoutSecretMaterial()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();

        var descriptor = DataSourceDescriptor.Create(
            tenantId,
            companyId,
            dataSourceId,
            "company.erp.production",
            "sql-server",
            "production",
            "primary-erp",
            allowRead: true,
            allowWrite: false,
            maxConcurrency: 8);

        Assert.Equal(tenantId, descriptor.TenantId);
        Assert.Equal(companyId, descriptor.CompanyId);
        Assert.Equal(dataSourceId, descriptor.Id);
        Assert.Equal("company.erp.production", descriptor.LogicalName);
        Assert.True(descriptor.AllowRead);
        Assert.False(descriptor.AllowWrite);
        Assert.Equal(8, descriptor.MaxConcurrency);

        Assert.DoesNotContain(
            typeof(DataSourceDescriptor).GetProperties(),
            property =>
                property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Connection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_RejectsDisabledAccessPolicy()
    {
        Assert.Throws<ArgumentException>(() =>
            DataSourceDescriptor.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "company.erp.production",
                "sql-server",
                "production",
                "primary-erp",
                allowRead: false,
                allowWrite: false,
                maxConcurrency: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public void Create_RejectsInvalidConcurrency(int maxConcurrency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DataSourceDescriptor.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "company.erp.production",
                "sql-server",
                "production",
                "primary-erp",
                allowRead: true,
                allowWrite: false,
                maxConcurrency));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Create_RejectsEmptyScopeIdentity(int emptyComponent)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        ids[emptyComponent] = Guid.Empty;

        Assert.Throws<ArgumentException>(() =>
            DataSourceDescriptor.Create(
                ids[0],
                ids[1],
                ids[2],
                "company.erp.production",
                "sql-server",
                "production",
                "primary-erp",
                allowRead: true,
                allowWrite: false,
                maxConcurrency: 1));
    }
}
