using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace Platform.Persistence.Tests;

public sealed class DataSourceRegistryManagementAuthorizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("viewer")]
    [InlineData("ADMIN")]
    [InlineData("unrecognized-role")]
    public async Task ActiveMemberWithoutCompanyAdmin_CannotChangePersistedSource(string? role)
    {
        var options = CreateOptions();
        var authorization = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedAsync(options, authorization);
        await using var context = new PlatformDbContext(options);
        var service = new DataSourceRegistryService(context, new EfAuthorizationDirectory(context));
        var created = await service.CreateAsync(authorization, Request("company.erp.production"));
        var before = await ReadStoredSourceAsync(options, created.Id);

        await SetRoleAsync(options, authorization, role);
        Assert.Equal(created.Id, Assert.Single(await service.ListAsync(authorization)).Id);
        var deniedRequest = Request("company.erp.denied") with
        {
            Kind = "changed-kind",
            Environment = "changed-environment",
            Purpose = "changed-purpose",
            ConnectionSecretReference = "secretref://env/denied-rotation",
            AllowRead = false,
            AllowWrite = true,
            MaxConcurrency = 16,
            IsEnabled = false
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.CreateAsync(authorization, deniedRequest));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.UpdateAsync(authorization, created.Id, deniedRequest));

        await using var verification = new PlatformDbContext(options);
        Assert.Equal(1, await verification.DataSources.CountAsync());
        Assert.Equivalent(before, await verification.DataSources.AsNoTracking().SingleAsync(), strict: true);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("company")]
    [InlineData("membership")]
    public async Task InactiveDirectoryEntry_CannotReadOrManageEvenWithAdminAssignment(string inactiveEntry)
    {
        var options = CreateOptions();
        var authorization = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedAsync(options, authorization);
        await using var context = new PlatformDbContext(options);
        var service = new DataSourceRegistryService(context, new EfAuthorizationDirectory(context));
        var created = await service.CreateAsync(authorization, Request("company.erp.production"));
        var before = await ReadStoredSourceAsync(options, created.Id);

        await using (var directory = new PlatformDbContext(options))
        {
            if (inactiveEntry == "user") (await directory.Users.SingleAsync()).IsActive = false;
            if (inactiveEntry == "company") (await directory.Companies.SingleAsync()).IsActive = false;
            if (inactiveEntry == "membership") (await directory.CompanyMemberships.SingleAsync()).IsActive = false;
            await directory.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.ListAsync(authorization));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.CreateAsync(authorization, Request("company.erp.denied")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.UpdateAsync(authorization, created.Id, Request("company.erp.changed")));
        await using var verification = new PlatformDbContext(options);
        Assert.Single(await verification.DataSources.ToArrayAsync());
        Assert.Equivalent(before, await verification.DataSources.AsNoTracking().SingleAsync(), strict: true);
    }

    [Fact]
    public async Task ExistingService_ObservesRoleGrantAndRevocation()
    {
        var options = CreateOptions();
        var authorization = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedAsync(options, authorization, "viewer");
        await using var context = new PlatformDbContext(options);
        var service = new DataSourceRegistryService(context, new EfAuthorizationDirectory(context));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.CreateAsync(authorization, Request("company.erp.production")));
        await SetRoleAsync(options, authorization, "admin");
        var created = await service.CreateAsync(authorization, Request("company.erp.production"));
        var before = await ReadStoredSourceAsync(options, created.Id);

        await SetRoleAsync(options, authorization, "viewer");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.UpdateAsync(authorization, created.Id, Request("company.erp.changed")));
        Assert.Equal(created.Id, Assert.Single(await service.ListAsync(authorization)).Id);
        Assert.Equivalent(before, await ReadStoredSourceAsync(options, created.Id), strict: true);
    }

    [Fact]
    public async Task AdminInAnotherCompany_DoesNotGrantTargetCompanyManagement()
    {
        var options = CreateOptions();
        var authorization = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var otherCompanyId = Guid.NewGuid();
        await SeedAsync(options, authorization, "viewer");
        await using (var directory = new PlatformDbContext(options))
        {
            directory.Companies.Add(new CompanyRecord
            {
                TenantId = authorization.TenantId,
                Id = otherCompanyId,
                Code = $"C-{otherCompanyId:N}",
                Name = "Other company",
                IsActive = true
            });
            directory.CompanyMemberships.Add(new CompanyMembershipRecord
            {
                TenantId = authorization.TenantId,
                CompanyId = otherCompanyId,
                UserId = authorization.UserId,
                IsActive = true
            });
            directory.RoleAssignments.Add(new RoleAssignmentRecord
            {
                TenantId = authorization.TenantId,
                CompanyId = otherCompanyId,
                UserId = authorization.UserId,
                RoleKey = "admin",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await directory.SaveChangesAsync();
        }

        await using var context = new PlatformDbContext(options);
        var service = new DataSourceRegistryService(context, new EfAuthorizationDirectory(context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.CreateAsync(authorization, Request("company.erp.production")));
        Assert.Empty(await service.ListAsync(authorization));
        var created = await service.CreateAsync(
            AuthorizationContext.Create(authorization.TenantId, otherCompanyId, authorization.UserId),
            Request("company.erp.production"));

        await using var verification = new PlatformDbContext(options);
        var stored = await verification.DataSources.AsNoTracking().SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(otherCompanyId, stored.CompanyId);
    }

    private static DbContextOptions<PlatformDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"registry-management-{Guid.NewGuid():N}")
            .Options;

    private static async Task SeedAsync(
        DbContextOptions<PlatformDbContext> options,
        AuthorizationContext authorization,
        string? role = "admin")
    {
        await using var context = new PlatformDbContext(options);
        context.Users.Add(new PlatformUserRecord
        {
            TenantId = authorization.TenantId,
            Id = authorization.UserId,
            IdentityProvider = "test",
            Subject = $"subject-{authorization.UserId:N}",
            DisplayName = "Member",
            IsActive = true
        });
        context.Companies.Add(new CompanyRecord
        {
            TenantId = authorization.TenantId,
            Id = authorization.CompanyId,
            Code = $"C-{authorization.CompanyId:N}",
            Name = "Company",
            IsActive = true
        });
        context.CompanyMemberships.Add(new CompanyMembershipRecord
        {
            TenantId = authorization.TenantId,
            CompanyId = authorization.CompanyId,
            UserId = authorization.UserId,
            IsActive = true
        });
        if (role is not null)
        {
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                TenantId = authorization.TenantId,
                CompanyId = authorization.CompanyId,
                UserId = authorization.UserId,
                RoleKey = role,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        await context.SaveChangesAsync();
    }

    private static async Task SetRoleAsync(
        DbContextOptions<PlatformDbContext> options,
        AuthorizationContext authorization,
        string? role)
    {
        await using var directory = new PlatformDbContext(options);
        var assignments = await directory.RoleAssignments.Where(assignment =>
            assignment.TenantId == authorization.TenantId
            && assignment.CompanyId == authorization.CompanyId
            && assignment.UserId == authorization.UserId).ToArrayAsync();
        directory.RoleAssignments.RemoveRange(assignments);
        await directory.SaveChangesAsync();
        if (role is not null)
        {
            directory.RoleAssignments.Add(new RoleAssignmentRecord
            {
                TenantId = authorization.TenantId,
                CompanyId = authorization.CompanyId,
                UserId = authorization.UserId,
                RoleKey = role,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await directory.SaveChangesAsync();
        }
    }

    private static async Task<DataSourceRecord> ReadStoredSourceAsync(
        DbContextOptions<PlatformDbContext> options,
        Guid sourceId)
    {
        await using var verification = new PlatformDbContext(options);
        return await verification.DataSources.AsNoTracking().SingleAsync(source => source.Id == sourceId);
    }

    private static DataSourceRegistryWriteRequest Request(string name) =>
        new(name, "sql-server", "production", "primary-erp", "secretref://env/original",
            AllowRead: true, AllowWrite: false, MaxConcurrency: 2);
}
