using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class DataSourceRegistryServiceTests
{
    [Fact]
    public async Task CreateAndList_StayCompanyScopedAndDoNotExposeSecretReference()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);
        var service = CreateService(context);
        var authorization = AuthorizationContext.Create(
            tenantId,
            companyId,
            userId);

        var created = await service.CreateAsync(
            authorization,
            CreateRequest("  company.erp.production  "));

        var listed = await service.ListAsync(authorization);
        var stored = await context.DataSources.SingleAsync();

        Assert.Equal(created.Id, Assert.Single(listed).Id);
        Assert.Equal("company.erp.production", created.LogicalName);
        Assert.Equal("secretref://env/company-erp-production", stored.ConnectionSecretReference);
        Assert.DoesNotContain(
            typeof(DataSourceDescriptor).GetProperties(),
            property =>
                property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Connection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_NonMemberFailsClosed()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.CreateAsync(
                AuthorizationContext.Create(
                    tenantId,
                    companyId,
                    Guid.NewGuid()),
                CreateRequest("company.erp.production")));

        Assert.Empty(context.DataSources);
    }

    [Fact]
    public async Task Create_RejectsDuplicateLogicalNameInsideCompany()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);
        var service = CreateService(context);
        var authorization = AuthorizationContext.Create(
            tenantId,
            companyId,
            userId);

        await service.CreateAsync(
            authorization,
            CreateRequest("company.erp.production"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CreateAsync(
                authorization,
                CreateRequest("company.erp.production")));
    }

    [Fact]
    public async Task SameLogicalName_IsAllowedAcrossDifferentCompanies()
    {
        var tenantId = Guid.NewGuid();
        var firstCompanyId = Guid.NewGuid();
        var secondCompanyId = Guid.NewGuid();
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(
            context,
            tenantId,
            firstCompanyId,
            firstUserId);
        await SeedAuthorizationAsync(
            context,
            tenantId,
            secondCompanyId,
            secondUserId);
        var service = CreateService(context);

        var first = await service.CreateAsync(
            AuthorizationContext.Create(
                tenantId,
                firstCompanyId,
                firstUserId),
            CreateRequest("company.erp.production"));
        var second = await service.CreateAsync(
            AuthorizationContext.Create(
                tenantId,
                secondCompanyId,
                secondUserId),
            CreateRequest("company.erp.production"));

        Assert.NotEqual(first.CompanyId, second.CompanyId);
        Assert.Equal(2, await context.DataSources.CountAsync());
    }

    [Fact]
    public async Task Update_CannotReachAnotherCompanyDataSource()
    {
        var tenantId = Guid.NewGuid();
        var owningCompanyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var owningUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(
            context,
            tenantId,
            owningCompanyId,
            owningUserId);
        await SeedAuthorizationAsync(
            context,
            tenantId,
            otherCompanyId,
            otherUserId);
        var service = CreateService(context);

        var created = await service.CreateAsync(
            AuthorizationContext.Create(
                tenantId,
                owningCompanyId,
                owningUserId),
            CreateRequest("company.erp.production"));

        var result = await service.UpdateAsync(
            AuthorizationContext.Create(
                tenantId,
                otherCompanyId,
                otherUserId),
            created.Id,
            CreateRequest("company.erp.changed"));

        var stored = await context.DataSources.SingleAsync();

        Assert.Null(result);
        Assert.Equal("company.erp.production", stored.LogicalName);
    }

    [Fact]
    public async Task Create_RejectsNonSecretReferenceCredentialInput()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);
        var service = CreateService(context);
        var authorization = AuthorizationContext.Create(
            tenantId,
            companyId,
            userId);

        var request = CreateRequest("company.erp.production") with
        {
            ConnectionSecretReference = "runtime-credential-material"
        };

        await Assert.ThrowsAsync<FormatException>(async () =>
            await service.CreateAsync(authorization, request));

        Assert.Empty(context.DataSources);
    }

    [Fact]
    public async Task Update_MetadataWithoutSecretReference_PreservesStoredSecretReference()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);
        var service = CreateService(context);
        var authorization = AuthorizationContext.Create(
            tenantId,
            companyId,
            userId);

        var created = await service.CreateAsync(
            authorization,
            CreateRequest("company.erp.production"));

        var updated = await service.UpdateAsync(
            authorization,
            created.Id,
            CreateRequest("company.erp.reporting") with
            {
                ConnectionSecretReference = null,
                MaxConcurrency = 8
            });

        var stored = await context.DataSources.SingleAsync();

        Assert.NotNull(updated);
        Assert.Equal("company.erp.reporting", updated.LogicalName);
        Assert.Equal(8, updated.MaxConcurrency);
        Assert.Equal(
            "secretref://env/company-erp-production",
            stored.ConnectionSecretReference);
    }

    [Fact]
    public async Task Update_CanRotateSecretReferenceWithoutReturningIt()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var context = CreateContext();
        await SeedAuthorizationAsync(context, tenantId, companyId, userId);
        var service = CreateService(context);
        var authorization = AuthorizationContext.Create(
            tenantId,
            companyId,
            userId);

        var created = await service.CreateAsync(
            authorization,
            CreateRequest("company.erp.production"));

        var updated = await service.UpdateAsync(
            authorization,
            created.Id,
            CreateRequest("company.erp.production") with
            {
                ConnectionSecretReference = "secretref://env/company-erp-production-v2",
                MaxConcurrency = 12
            });

        var stored = await context.DataSources.SingleAsync();

        Assert.NotNull(updated);
        Assert.Equal(12, updated.MaxConcurrency);
        Assert.Equal("secretref://env/company-erp-production-v2", stored.ConnectionSecretReference);
        Assert.DoesNotContain("secretref://", updated.ToString());
    }

    private static DataSourceRegistryService CreateService(
        PlatformDbContext context) =>
        new(context, new EfAuthorizationDirectory(context));

    private static async Task SeedAuthorizationAsync(
        PlatformDbContext context,
        Guid tenantId,
        Guid companyId,
        Guid userId)
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
            IsActive = true
        });

        await context.SaveChangesAsync();
    }

    private static DataSourceRegistryWriteRequest CreateRequest(string logicalName) =>
        new(
            logicalName,
            "sql-server",
            "production",
            "primary-erp",
            "secretref://env/company-erp-production",
            AllowRead: true,
            AllowWrite: false,
            MaxConcurrency: 4);

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"data-source-registry-{Guid.NewGuid():N}")
            .Options;

        return new PlatformDbContext(options);
    }
}
