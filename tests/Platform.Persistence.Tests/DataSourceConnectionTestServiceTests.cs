using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class DataSourceConnectionTestServiceTests
{
    [Fact]
    public async Task TestAsync_UsesAuthorizationScopeAndKeepsSecretMaterialOutOfResult()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();

        context.DataSources.Add(CreateDataSource(
            tenantId,
            companyId,
            dataSourceId,
            "secretref://env/company-erp-production"));
        await context.SaveChangesAsync();

        var resolver = new RecordingSecretResolver("opaque-runtime-connection-material");
        var probe = new RecordingProbe();
        var service = CreateService(context, resolver, probe);

        var result = await service.TestAsync(
            AuthorizationContext.Create(tenantId, companyId, Guid.NewGuid()),
            dataSourceId);

        Assert.True(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.Success, result.Code);
        Assert.Equal("company-erp-production", resolver.LastReference?.Resource);
        Assert.Equal("opaque-runtime-connection-material", probe.LastConnectionString);
        Assert.DoesNotContain("opaque-runtime-connection-material", result.Message);
        Assert.DoesNotContain("secretref://", result.Message);
    }

    [Fact]
    public async Task TestAsync_DoesNotCrossCompanyBoundary()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();

        context.DataSources.Add(CreateDataSource(
            tenantId,
            companyId,
            dataSourceId,
            "secretref://env/company-erp-production"));
        await context.SaveChangesAsync();

        var resolver = new RecordingSecretResolver("opaque-runtime-connection-material");
        var probe = new RecordingProbe();
        var service = CreateService(context, resolver, probe);

        var result = await service.TestAsync(
            AuthorizationContext.Create(tenantId, Guid.NewGuid(), Guid.NewGuid()),
            dataSourceId);

        Assert.False(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.NotFound, result.Code);
        Assert.Null(resolver.LastReference);
        Assert.Null(probe.LastConnectionString);
    }

    [Fact]
    public async Task TestAsync_DisabledDataSourceDoesNotResolveSecret()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();

        var dataSource = CreateDataSource(
            tenantId,
            companyId,
            dataSourceId,
            "secretref://env/company-erp-production");
        dataSource.IsEnabled = false;
        context.DataSources.Add(dataSource);
        await context.SaveChangesAsync();

        var resolver = new RecordingSecretResolver("opaque-runtime-connection-material");
        var probe = new RecordingProbe();
        var service = CreateService(context, resolver, probe);

        var result = await service.TestAsync(
            AuthorizationContext.Create(tenantId, companyId, Guid.NewGuid()),
            dataSourceId);

        Assert.False(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.Disabled, result.Code);
        Assert.Null(resolver.LastReference);
        Assert.Null(probe.LastConnectionString);
    }

    [Fact]
    public async Task TestAsync_RejectsInvalidSecretReferenceBeforeResolution()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();

        context.DataSources.Add(CreateDataSource(
            tenantId,
            companyId,
            dataSourceId,
            "not-a-secret-reference"));
        await context.SaveChangesAsync();

        var resolver = new RecordingSecretResolver("opaque-runtime-connection-material");
        var probe = new RecordingProbe();
        var service = CreateService(context, resolver, probe);

        var result = await service.TestAsync(
            AuthorizationContext.Create(tenantId, companyId, Guid.NewGuid()),
            dataSourceId);

        Assert.False(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.InvalidConfiguration, result.Code);
        Assert.Null(resolver.LastReference);
        Assert.Null(probe.LastConnectionString);
    }

    [Fact]
    public async Task TestAsync_SanitizesConnectionProbeFailure()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();

        context.DataSources.Add(CreateDataSource(
            tenantId,
            companyId,
            dataSourceId,
            "secretref://env/company-erp-production"));
        await context.SaveChangesAsync();

        var resolver = new RecordingSecretResolver("opaque-runtime-connection-material");
        var probe = new RecordingProbe("sensitive-probe-detail");
        var service = CreateService(context, resolver, probe);

        var result = await service.TestAsync(
            AuthorizationContext.Create(tenantId, companyId, Guid.NewGuid()),
            dataSourceId);

        Assert.False(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.ConnectionFailed, result.Code);
        Assert.DoesNotContain("sensitive-probe-detail", result.Message);
        Assert.DoesNotContain("opaque-runtime-connection-material", result.Message);
    }

    private static DataSourceConnectionTestService CreateService(
        PlatformDbContext context,
        RecordingSecretResolver resolver,
        RecordingProbe probe) =>
        new(
            context,
            new CompositeSecretResolver(new[] { resolver }),
            probe);

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"data-source-test-{Guid.NewGuid():N}")
            .Options;

        return new PlatformDbContext(options);
    }

    private static DataSourceRecord CreateDataSource(
        Guid tenantId,
        Guid companyId,
        Guid id,
        string secretReference) =>
        new()
        {
            TenantId = tenantId,
            CompanyId = companyId,
            Id = id,
            LogicalName = "company.erp.production",
            Kind = "sql-server",
            Environment = "production",
            Purpose = "primary-erp",
            ConnectionSecretReference = secretReference,
            AllowRead = true,
            AllowWrite = false,
            MaxConcurrency = 4,
            IsEnabled = true
        };

    private sealed class RecordingSecretResolver(string value) : ISecretResolver
    {
        public string Provider => "env";

        public SecretReference? LastReference { get; private set; }

        public ValueTask<string> ResolveAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastReference = reference;
            return ValueTask.FromResult(value);
        }
    }

    private sealed class RecordingProbe(string? failureDetail = null) : IDataSourceConnectionProbe
    {
        public string? LastConnectionString { get; private set; }

        public ValueTask ProbeAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastConnectionString = connectionString;

            if (failureDetail is not null)
            {
                throw new InvalidOperationException(failureDetail);
            }

            return ValueTask.CompletedTask;
        }
    }
}
