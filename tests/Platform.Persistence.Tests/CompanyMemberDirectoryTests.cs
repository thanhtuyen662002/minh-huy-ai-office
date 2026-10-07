using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

internal static class MemberDirectoryFixture
{
    public static void Seed(PlatformDbContext db, AuthorizationContext scope, string? role = "admin")
    {
        db.Companies.Add(new() { TenantId = scope.TenantId, Id = scope.CompanyId, Code = "C", Name = "Company" });
        AddMember(db, scope, scope.UserId, "Owner", roles: role is null ? [] : [role]);
    }
    public static void AddMember(PlatformDbContext db, AuthorizationContext scope, Guid id, string name,
        bool userActive = true, bool membershipActive = true, string[]? roles = null)
    {
        db.Users.Add(new() { TenantId = scope.TenantId, Id = id, DisplayName = name, IdentityProvider = "PRIVATE_PROVIDER", Subject = "PRIVATE_SUBJECT_" + id, IsActive = userActive });
        db.CompanyMemberships.Add(new() { TenantId = scope.TenantId, CompanyId = scope.CompanyId, UserId = id, IsActive = membershipActive });
        foreach (var role in roles ?? []) db.RoleAssignments.Add(new() { TenantId = scope.TenantId, CompanyId = scope.CompanyId, UserId = id, RoleKey = role });
    }
    public static DbContextOptions<PlatformDbContext> Options() => new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    public static AuthorizationContext Authority() => AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
}

public sealed class CompanyMemberDirectoryTests
{
    [Fact]
    public async Task AdminReadsScopedStatusAndRolesWithoutIdentityDetails()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options());
        var scope = MemberDirectoryFixture.Authority(); MemberDirectoryFixture.Seed(db, scope);
        MemberDirectoryFixture.AddMember(db, scope, Guid.NewGuid(), "Dormant", false, false, ["viewer"]);
        MemberDirectoryFixture.AddMember(db, MemberDirectoryFixture.Authority(), Guid.NewGuid(), "FOREIGN", roles: ["admin"]);
        await db.SaveChangesAsync();
        var result = await new CompanyMemberDirectory(db, new EfAuthorizationDirectory(db)).ListAsync(scope);
        Assert.Equal(scope.CompanyId, result.CompanyId); Assert.Equal(2, result.Items.Count); Assert.False(result.HasMore);
        var dormant = Assert.Single(result.Items, item => item.DisplayName == "Dormant");
        Assert.False(dormant.UserActive); Assert.False(dormant.MembershipActive); Assert.Equal(new[] { "viewer" }, dormant.Roles);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(result)); Assert.DoesNotContain("FOREIGN", JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("viewer")]
    [InlineData("ADMIN")]
    public async Task TokenOrNonAdminRoleDoesNotGrantDirectory(string? role)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope, role); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CompanyMemberDirectory(db, new EfAuthorizationDirectory(db)).ListAsync(scope));
    }
    [Theory]
    [InlineData("user")]
    [InlineData("company")]
    [InlineData("membership")]
    public async Task InactiveAuthorityCannotRead(string inactive)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope);
        if (inactive == "user") db.Users.Local.Single().IsActive = false;
        if (inactive == "company") db.Companies.Local.Single().IsActive = false;
        if (inactive == "membership") db.CompanyMemberships.Local.Single().IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CompanyMemberDirectory(db, new EfAuthorizationDirectory(db)).ListAsync(scope));
    }
    [Fact]
    public async Task ScopeFilterPrecedesStablePaginationAndForeignRolesDoNotAppear()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope);
        for (var i = 0; i < 4; i++) MemberDirectoryFixture.AddMember(db, scope, Guid.NewGuid(), "Local" + i);
        for (var i = 0; i < 8; i++) MemberDirectoryFixture.AddMember(db, MemberDirectoryFixture.Authority(), Guid.NewGuid(), "FOREIGN" + i);
        db.RoleAssignments.Add(new() { TenantId = scope.TenantId, CompanyId = Guid.NewGuid(), UserId = scope.UserId, RoleKey = "FOREIGN_ROLE" });
        await db.SaveChangesAsync(); var service = new CompanyMemberDirectory(db, new EfAuthorizationDirectory(db));
        var all = await service.ListAsync(scope); var first = await service.ListAsync(scope, 0, 2); var second = await service.ListAsync(scope, 2, 2); var final = await service.ListAsync(scope, 4, 2);
        Assert.True(first.HasMore); Assert.True(second.HasMore); Assert.False(final.HasMore);
        Assert.Equal(all.Items.Select(item => item.UserId), first.Items.Concat(second.Items).Concat(final.Items).Select(item => item.UserId));
        Assert.DoesNotContain("FOREIGN", JsonSerializer.Serialize(all));
    }
    [Fact]
    public async Task ExistingServiceObservesGrantRevocationWithoutCachedAuthority()
    {
        var options = MemberDirectoryFixture.Options(); var scope = MemberDirectoryFixture.Authority();
        await using var db = new PlatformDbContext(options); MemberDirectoryFixture.Seed(db, scope); await db.SaveChangesAsync();
        var service = new CompanyMemberDirectory(db, new EfAuthorizationDirectory(db)); Assert.Single((await service.ListAsync(scope)).Items);
        await using var other = new PlatformDbContext(options); other.RoleAssignments.RemoveRange(await other.RoleAssignments.ToArrayAsync()); await other.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(scope));
    }
    [Theory]
    [InlineData(-1, 25)]
    [InlineData(1001, 25)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task PaginationIsBounded(int offset, int limit)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority(); MemberDirectoryFixture.Seed(db, scope); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new CompanyMemberDirectory(db, new EfAuthorizationDirectory(db)).ListAsync(scope, offset, limit));
    }
    [Fact]
    public async Task OversizedRoleCollectionFailsInsteadOfReturningIncompleteRoles()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority(); MemberDirectoryFixture.Seed(db, scope);
        MemberDirectoryFixture.AddMember(db, scope, Guid.NewGuid(), "Many roles", roles: Enumerable.Range(0, 257).Select(i => "role" + i).ToArray()); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CompanyMemberDirectory(db, new EfAuthorizationDirectory(db)).ListAsync(scope));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedDuringReadOrMismatchedDirectoryContextReturnsNothing(bool mismatched)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority(); MemberDirectoryFixture.Seed(db, scope); await db.SaveChangesAsync();
        var directory = new ChangingDirectory(scope, mismatched);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CompanyMemberDirectory(db, directory).ListAsync(scope));
        Assert.Equal(mismatched ? 1 : 2, directory.Calls);
    }
    private sealed class ChangingDirectory(AuthorizationContext scope, bool mismatched) : IAuthorizationDirectory
    {
        public int Calls { get; private set; }
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<AuthorizationDirectoryEntry?>(mismatched ? new(MemberDirectoryFixture.Authority(), ["admin"]) : Calls == 1 ? new(scope, ["admin"]) : null); }
    }
}
