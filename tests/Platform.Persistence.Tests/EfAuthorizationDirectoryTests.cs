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

        context.Users.Add(new PlatformUserRecord { TenantId = tenantId, Id = userId, IdentityProvider = "test", Subject = "user-1", DisplayName = "User One", IsActive = true });
        context.Companies.AddRange(
            new CompanyRecord { TenantId = tenantId, Id = firstCompanyId, Code = "COMPANY-A", Name = "Company A", IsActive = true },
            new CompanyRecord { TenantId = tenantId, Id = secondCompanyId, Code = "COMPANY-B", Name = "Company B", IsActive = true });
        context.CompanyMemberships.AddRange(
            new CompanyMembershipRecord { TenantId = tenantId, CompanyId = firstCompanyId, UserId = userId, IsActive = true },
            new CompanyMembershipRecord { TenantId = tenantId, CompanyId = secondCompanyId, UserId = userId, IsActive = true });
        context.RoleAssignments.AddRange(
            new RoleAssignmentRecord { TenantId = tenantId, CompanyId = firstCompanyId, UserId = userId, RoleKey = "admin" },
            new RoleAssignmentRecord { TenantId = tenantId, CompanyId = secondCompanyId, UserId = userId, RoleKey = "viewer" });
        await context.SaveChangesAsync();

        var directory = new EfAuthorizationDirectory(context);

        var first = await directory.ResolveAsync(AuthorizationContext.Create(tenantId, firstCompanyId, userId));
        var second = await directory.ResolveAsync(AuthorizationContext.Create(tenantId, secondCompanyId, userId));

        Assert.NotNull(first);
        Assert.Equal(new[] { "admin" }, first.Roles);
        Assert.NotNull(second);
        Assert.Equal(new[] { "viewer" }, second.Roles);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsEntryWithNoRolesForActiveMembership()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        SeedActiveIdentity(context, tenantId, companyId, userId);
        await context.SaveChangesAsync();

        var result = await new EfAuthorizationDirectory(context).ResolveAsync(
            AuthorizationContext.Create(tenantId, companyId, userId));

        Assert.NotNull(result);
        Assert.Empty(result.Roles);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenActiveMembershipDoesNotExist()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var allowedCompanyId = Guid.NewGuid();
        var deniedCompanyId = Guid.NewGuid();

        context.Users.Add(new PlatformUserRecord { TenantId = tenantId, Id = userId, IdentityProvider = "test", Subject = "user-1", DisplayName = "User One", IsActive = true });
        context.Companies.AddRange(
            new CompanyRecord { TenantId = tenantId, Id = allowedCompanyId, Code = "ALLOWED", Name = "Allowed", IsActive = true },
            new CompanyRecord { TenantId = tenantId, Id = deniedCompanyId, Code = "DENIED", Name = "Denied", IsActive = true });
        context.CompanyMemberships.Add(new CompanyMembershipRecord { TenantId = tenantId, CompanyId = allowedCompanyId, UserId = userId, IsActive = true });
        await context.SaveChangesAsync();

        var denied = await new EfAuthorizationDirectory(context).ResolveAsync(
            AuthorizationContext.Create(tenantId, deniedCompanyId, userId));

        Assert.Null(denied);
    }

    [Fact]
    public async Task ResolveAsync_FailsClosedAfterMembershipRevocationEvenWhenRoleRemains()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var membership = SeedActiveIdentity(context, tenantId, companyId, userId);
        context.RoleAssignments.Add(new RoleAssignmentRecord { TenantId = tenantId, CompanyId = companyId, UserId = userId, RoleKey = "admin" });
        await context.SaveChangesAsync();

        var directory = new EfAuthorizationDirectory(context);
        var authorizationContext = AuthorizationContext.Create(tenantId, companyId, userId);
        var beforeRevocation = await directory.ResolveAsync(authorizationContext);
        Assert.NotNull(beforeRevocation);
        Assert.Equal(new[] { "admin" }, beforeRevocation.Roles);

        membership.IsActive = false;
        await context.SaveChangesAsync();

        Assert.Null(await directory.ResolveAsync(authorizationContext));
        Assert.True(await context.RoleAssignments.AnyAsync(role => role.RoleKey == "admin"));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenUserCompanyOrMembershipIsInactive()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var membership = SeedActiveIdentity(context, tenantId, companyId, userId);
        await context.SaveChangesAsync();
        var user = await context.Users.SingleAsync();
        var company = await context.Companies.SingleAsync();

        var directory = new EfAuthorizationDirectory(context);
        var authorizationContext = AuthorizationContext.Create(tenantId, companyId, userId);

        membership.IsActive = false;
        await context.SaveChangesAsync();
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

    private static CompanyMembershipRecord SeedActiveIdentity(
        PlatformDbContext context,
        Guid tenantId,
        Guid companyId,
        Guid userId)
    {
        context.Users.Add(new PlatformUserRecord { TenantId = tenantId, Id = userId, IdentityProvider = "test", Subject = "user-1", DisplayName = "User One", IsActive = true });
        context.Companies.Add(new CompanyRecord { TenantId = tenantId, Id = companyId, Code = "COMPANY", Name = "Company", IsActive = true });
        var membership = new CompanyMembershipRecord { TenantId = tenantId, CompanyId = companyId, UserId = userId, IsActive = true };
        context.CompanyMemberships.Add(membership);
        return membership;
    }

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"identity-{Guid.NewGuid():N}")
            .Options;

        return new PlatformDbContext(options);
    }
}
