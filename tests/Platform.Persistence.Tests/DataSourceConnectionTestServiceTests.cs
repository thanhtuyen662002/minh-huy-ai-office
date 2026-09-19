using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class DataSourceConnectionTestServiceTests
{
    [Fact]
    public async Task TestAsync_UsesActiveMembershipAndKeepsSecretMaterialOutOfResult()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);

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
            AuthorizationContext.Create(tenantId, companyId, userId),
            dataSourceId);

        Assert.True(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.Success, result.Code);
        Assert.Equal("company-erp-production", resolver.LastReference?.Resource);
        Assert.Equal("opaque-runtime-connection-material", probe.LastConnectionString);
        Assert.DoesNotContain("opaque-runtime-connection-material", result.Message);
        Assert.DoesNotContain("secretref://", result.Message);
    }

    [Fact]
    public async Task TestAsync_NonMemberFailsBeforeSecretResolution()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var authorizedUserId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(
            context,
            tenantId,
            companyId,
            authorizedUserId);

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
            AuthorizationContext.Create(
                tenantId,
                companyId,
                Guid.NewGuid()),
            dataSourceId);

        Assert.False(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.NotAuthorized, result.Code);
        Assert.Null(resolver.LastReference);
        Assert.Null(probe.LastConnectionString);
    }

    [Fact]
    public async Task TestAsync_InactiveMembershipFailsBeforeSecretResolution()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(
            context,
            tenantId,
            companyId,
            userId,
            membershipActive: false);

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
            AuthorizationContext.Create(tenantId, companyId, userId),
            dataSourceId);

        Assert.False(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.NotAuthorized, result.Code);
        Assert.Null(resolver.LastReference);
        Assert.Null(probe.LastConnectionString);
    }

    [Fact]
    public async Task TestAsync_DoesNotCrossCompanyBoundary()
    {
        var tenantId = Guid.NewGuid();
        var allowedCompanyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(
            context,
            tenantId,
            allowedCompanyId,
            userId);

        context.Companies.Add(new CompanyRecord
        {
            TenantId = tenantId,
            Id = otherCompanyId,
            Code = "OTHER",
            Name = "Other",
            IsActive = true
        });
        context.DataSources.Add(CreateDataSource(
            tenantId,
            allowedCompanyId,
            dataSourceId,
            "secretref://env/company-erp-production"));
        await context.SaveChangesAsync();

        var resolver = new RecordingSecretResolver("opaque-runtime-connection-material");
        var probe = new RecordingProbe();
        var service = CreateService(context, resolver, probe);

        var result = await service.TestAsync(
            AuthorizationContext.Create(tenantId, otherCompanyId, userId),
            dataSourceId);

        Assert.False(result.Succeeded);
        Assert.Equal(DataSourceConnectionTestCodes.NotAuthorized, result.Code);
        Assert.Null(resolver.LastReference);
        Assert.Null(probe.LastConnectionString);
    }

    [Fact]
    public async Task TestAsync_DisabledDataSourceDoesNotResolveSecret()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);

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
            AuthorizationContext.Create(tenantId, companyId, userId),
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
        var userId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);

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
            AuthorizationContext.Create(tenantId, companyId, userId),
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
        var userId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);

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
            AuthorizationContext.Create(tenantId, companyId, userId),
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
            new EfAuthorizationDirectory(context),
            new CompositeSecretResolver(new[] { resolver }),
            probe);

    private static async Task SeedAuthorizationAsync(
        PlatformDbContext context,
        Guid tenantId,
        Guid companyId,
        Guid userId,
        bool membershipActive = true)
    {
        context.Users.Add(new PlatformUserRecord
        {
            TenantId = tenantId,
            Id = userId,
            IdentityProvider = "test",
            Subject = $"user-{userId:N}",
            DisplayName = "User",
            IsActive = true
        });
        context.Companies.Add(new CompanyRecord
        {
            TenantId = tenantId,
            Id = companyId,
            Code = $"C-{companyId:N}",
            Name = "Company",
            IsActive = true
        });
        context.CompanyMemberships.Add(new CompanyMembershipRecord
        {
            TenantId = tenantId,
            CompanyId = companyId,
            UserId = userId,
            IsActive = membershipActive
        });

        await context.SaveChangesAsync();
    }

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
