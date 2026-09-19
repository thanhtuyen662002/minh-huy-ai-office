using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class EfAuthorizationDirectoryTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsRolesForTheSelectedTenantAndCompanyOnly()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var firstCompanyId = Guid.NewGuid();
        var secondCompanyId = Guid.NewGuid();

        context.Users.Add(
            new PlatformUserRecord
            {
                TenantId = tenantId,
                Id = userId,
                IdentityProvider = "test",
                Subject = "user-1",
                DisplayName = "User One",
                IsActive = true
            });
        context.Companies.AddRange(
            new CompanyRecord
            {
                TenantId = tenantId,
                Id = firstCompanyId,
                Code = "COMPANY-A",
                Name = "Company A",
                IsActive = true
            },
            new CompanyRecord
            {
                TenantId = tenantId,
                Id = secondCompanyId,
                Code = "COMPANY-B",
                Name = "Company B",
                IsActive = true
            });
        context.CompanyMemberships.AddRange(
            new CompanyMembershipRecord
            {
                TenantId = tenantId,
                CompanyId = firstCompanyId,
                UserId = userId,
                IsActive = true
            },
            new CompanyMembershipRecord
            {
                TenantId = tenantId,
                CompanyId = secondCompanyId,
                UserId = userId,
                IsActive = true
            });
        context.RoleAssignments.AddRange(
            new RoleAssignmentRecord
            {
                TenantId = tenantId,
                CompanyId = firstCompanyId,
                UserId = userId,
                RoleKey = "admin"
            },
            new RoleAssignmentRecord
            {
                TenantId = tenantId,
                CompanyId = secondCompanyId,
                UserId = userId,
                RoleKey = "viewer"
            });
        await context.SaveChangesAsync();

        var directory = new EfAuthorizationDirectory(context);

        var first = await directory.ResolveAsync(
            AuthorizationContext.Create(tenantId, firstCompanyId, userId));
        var second = await directory.ResolveAsync(
            AuthorizationContext.Create(tenantId, secondCompanyId, userId));

        Assert.NotNull(first);
        Assert.Equal(new[] { "admin" }, first.Roles);
        Assert.NotNull(second);
        Assert.Equal(new[] { "viewer" }, second.Roles);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenActiveMembershipDoesNotExist()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var allowedCompanyId = Guid.NewGuid();
        var deniedCompanyId = Guid.NewGuid();

        context.Users.Add(
            new PlatformUserRecord
            {
                TenantId = tenantId,
                Id = userId,
                IdentityProvider = "test",
                Subject = "user-1",
                DisplayName = "User One",
                IsActive = true
            });
        context.Companies.AddRange(
            new CompanyRecord
            {
                TenantId = tenantId,
                Id = allowedCompanyId,
                Code = "ALLOWED",
                Name = "Allowed",
                IsActive = true
            },
            new CompanyRecord
            {
                TenantId = tenantId,
                Id = deniedCompanyId,
                Code = "DENIED",
                Name = "Denied",
                IsActive = true
            });
        context.CompanyMemberships.Add(
            new CompanyMembershipRecord
            {
                TenantId = tenantId,
                CompanyId = allowedCompanyId,
                UserId = userId,
                IsActive = true
            });
        await context.SaveChangesAsync();

        var directory = new EfAuthorizationDirectory(context);

        var denied = await directory.ResolveAsync(
            AuthorizationContext.Create(tenantId, deniedCompanyId, userId));

        Assert.Null(denied);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenUserCompanyOrMembershipIsInactive()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var companyId = Guid.NewGuid();

        var user = new PlatformUserRecord
        {
            TenantId = tenantId,
            Id = userId,
            IdentityProvider = "test",
            Subject = "user-1",
            DisplayName = "User One",
            IsActive = true
        };
        var company = new CompanyRecord
        {
            TenantId = tenantId,
            Id = companyId,
            Code = "COMPANY",
            Name = "Company",
            IsActive = true
        };
        var membership = new CompanyMembershipRecord
        {
            TenantId = tenantId,
            CompanyId = companyId,
            UserId = userId,
            IsActive = false
        };

        context.Users.Add(user);
        context.Companies.Add(company);
        context.CompanyMemberships.Add(membership);
        await context.SaveChangesAsync();

        var directory = new EfAuthorizationDirectory(context);
        var authorizationContext = AuthorizationContext.Create(tenantId, companyId, userId);

        Assert.Null(await directory.ResolveAsync(authorizationContext));

        membership.IsActive = true;
        user.IsActive = false;
        await context.SaveChangesAsync();
        Assert.Null(await directory.ResolveAsync(authorizationContext));

        user.IsActive = true;
        company.IsActive = false;
        await context.SaveChangesAsync();
        Assert.Null(await directory.ResolveAsync(authorizationContext));
    }

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"identity-{Guid.NewGuid():N}")
            .Options;

        return new PlatformDbContext(options);
    }
}
