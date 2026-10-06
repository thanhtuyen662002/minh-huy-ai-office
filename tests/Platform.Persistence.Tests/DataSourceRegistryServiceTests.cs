using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataOnly_TwoContextsPreserveConcurrentPolicyAndRotation(bool afterServerLoad)
    {
        var root = new InMemoryDatabaseRoot();
        var name = $"metadata-race-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(name, root).Options;
        var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        DataSourceDescriptor initial;
        await using (var seed = new PlatformDbContext(options))
        {
            await SeedAuthorizationAsync(seed, authority.TenantId, authority.CompanyId, authority.UserId);
            initial = await CreateService(seed).CreateAsync(authority, CreateRequest("original"));
        }

        var barrier = new MetadataSaveBarrier(() => ChangeProtectedFieldsAsync(options, initial.Id));
        var metadataOptions = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(name, root).AddInterceptors(barrier).Options;
        await using var metadataContext = new PlatformDbContext(metadataOptions);
        if (afterServerLoad)
        {
            barrier.Enabled = true;
        }
        else
        {
            await ChangeProtectedFieldsAsync(options, initial.Id);
        }

        var updated = await CreateService(metadataContext).UpdateMetadataAsync(authority, initial.Id,
            new DataSourceMetadataWriteRequest(" renamed ", " new purpose ", 7, false));
        Assert.NotNull(updated);
        Assert.Equal("renamed", updated.LogicalName);
        Assert.Equal("new purpose", updated.Purpose);
        Assert.Equal("Postgres", updated.Kind);
        Assert.Equal("Production", updated.Environment);
        Assert.False(updated.AllowRead);
        Assert.True(updated.AllowWrite);
        Assert.False(updated.IsEnabled);
        Assert.Equal(7, updated.MaxConcurrency);
        Assert.DoesNotContain("secretref", updated.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var verify = new PlatformDbContext(options);
        var stored = await verify.DataSources.AsNoTracking().SingleAsync();
        Assert.Equal("secretref://env/fixture-rotated", stored.ConnectionSecretReference);
        Assert.Equal(authority.TenantId, stored.TenantId);
        Assert.Equal(authority.CompanyId, stored.CompanyId);
        Assert.Equal(initial.Id, stored.Id);
        Assert.Equal(afterServerLoad, barrier.Executed);
    }

    [Fact]
    public async Task MetadataOnly_RejectsDuplicateNameWithoutChangingEitherSource()
    {
        await using var context = CreateContext();
        var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedAuthorizationAsync(context, authority.TenantId, authority.CompanyId, authority.UserId);
        var service = CreateService(context);
        var first = await service.CreateAsync(authority, CreateRequest("first"));
        await service.CreateAsync(authority, CreateRequest("second"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.UpdateMetadataAsync(authority, first.Id, new("second", "changed", 2, false)));
        Assert.Equal("first", (await context.DataSources.AsNoTracking().SingleAsync(row => row.Id == first.Id)).LogicalName);
    }

    private static async Task ChangeProtectedFieldsAsync(DbContextOptions<PlatformDbContext> options, Guid id)
    {
        await using var concurrent = new PlatformDbContext(options);
        var source = await concurrent.DataSources.SingleAsync(row => row.Id == id);
        source.Kind = "Postgres";
        source.Environment = "Production";
        source.AllowRead = false;
        source.AllowWrite = true;
        source.ConnectionSecretReference = "secretref://env/fixture-rotated";
        await concurrent.SaveChangesAsync();
    }

    private sealed class MetadataSaveBarrier(Func<Task> concurrentWrite) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Executed { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Enabled = false;
                Executed = true;
                var context = eventData.Context!;
                context.ChangeTracker.DetectChanges();
                var entry = Assert.Single(context.ChangeTracker.Entries<DataSourceRecord>());
                foreach (var field in new[] { nameof(DataSourceRecord.Kind), nameof(DataSourceRecord.Environment),
                    nameof(DataSourceRecord.AllowRead), nameof(DataSourceRecord.AllowWrite), nameof(DataSourceRecord.ConnectionSecretReference),
                    nameof(DataSourceRecord.TenantId), nameof(DataSourceRecord.CompanyId), nameof(DataSourceRecord.Id), nameof(DataSourceRecord.CreatedAtUtc) })
                {
                    Assert.False(entry.Property(field).IsModified);
                }
                await concurrentWrite();
            }
            return result;
        }
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
        BindingFixture.Grant(context, tenantId, companyId, "secretref://env/company-erp-production");
        BindingFixture.Grant(context, tenantId, companyId, "secretref://env/company-erp-production-v2");
        BindingFixture.Grant(context, tenantId, companyId, "secretref://env/fixture-rotated");
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
        context.RoleAssignments.Add(new RoleAssignmentRecord
        {
            TenantId = tenantId,
            CompanyId = companyId,
            UserId = userId,
            RoleKey = "admin",
            CreatedAtUtc = DateTimeOffset.UtcNow
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
