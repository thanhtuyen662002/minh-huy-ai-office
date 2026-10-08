using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class AuthenticatedCompanyDirectoryTests
{
    [Fact]
    public async Task OwnActiveCompaniesAreListedWithoutIdentityRolesOrForeignInventory()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options());
        var scope = MemberDirectoryFixture.Authority(); MemberDirectoryFixture.Seed(db, scope);
        var second = AddCompany(db, scope.TenantId, scope.UserId, "Second company");
        MemberDirectoryFixture.Seed(db, MemberDirectoryFixture.Authority());
        db.Companies.Local.Last().Name = "FOREIGN";
        await db.SaveChangesAsync();
        var items = await Directory(db).ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + scope.UserId);
        Assert.Equal(new[] { scope.CompanyId, second }.Order(), items.Select(item => item.CompanyId));
        var json = JsonSerializer.Serialize(items); Assert.DoesNotContain("PRIVATE_", json);
        Assert.DoesNotContain("FOREIGN", json); Assert.DoesNotContain("admin", json);
        Assert.All(items, item => Assert.Equal(new[] { "CompanyId", "CompanyName" },
            JsonDocument.Parse(JsonSerializer.Serialize(item)).RootElement.EnumerateObject().Select(property => property.Name)));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("membership")]
    [InlineData("company")]
    public async Task InactiveOrRevokedScopesDisappearWithoutCachedChoices(string inactive)
    {
        var options = MemberDirectoryFixture.Options(); await using var db = new PlatformDbContext(options);
        var scope = MemberDirectoryFixture.Authority(); MemberDirectoryFixture.Seed(db, scope); await db.SaveChangesAsync();
        var directory = Directory(db); Assert.Single(await directory.ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + scope.UserId));
        await using (var other = new PlatformDbContext(options))
        {
            if (inactive == "user") (await other.Users.SingleAsync()).IsActive = false;
            if (inactive == "membership") (await other.CompanyMemberships.SingleAsync()).IsActive = false;
            if (inactive == "company") (await other.Companies.SingleAsync()).IsActive = false;
            await other.SaveChangesAsync();
        }
        Assert.Empty(await directory.ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + scope.UserId));
    }

    [Theory]
    [InlineData("opaqueProvider", "Subject")]
    [InlineData("OpaqueProvider", "subject")]
    [InlineData("OpaqueProvider ", "Subject")]
    [InlineData("OpaqueProvider", "Subject ")]
    public async Task DifferentOpaqueKeysDoNotDiscoverCompanies(string provider, string subject)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope); var user = db.Users.Local.Single();
        user.IdentityProvider = "OpaqueProvider"; user.Subject = "Subject"; await db.SaveChangesAsync();
        Assert.Empty(await Directory(db).ListAsync(provider, subject));
    }

    [Fact]
    public async Task ExactTrailingCharactersArePreserved()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope); var user = db.Users.Local.Single();
        user.IdentityProvider = "OpaqueProvider "; user.Subject = "Subject \u0130"; await db.SaveChangesAsync();
        Assert.Single(await Directory(db).ListAsync(user.IdentityProvider, user.Subject));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousCompanyIdsOrIdentityWithinTenantDenyTheWholeList(bool sameCompany)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var first = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, first);
        var second = MinhHuy.AIOffice.Shared.Contracts.AuthorizationContext.Create(
            sameCompany ? Guid.NewGuid() : first.TenantId, sameCompany ? first.CompanyId : Guid.NewGuid(), Guid.NewGuid());
        MemberDirectoryFixture.Seed(db, second); var user = db.Users.Local.Single(row => row.Id == second.UserId);
        user.Subject = "PRIVATE_SUBJECT_" + first.UserId; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Directory(db).ListAsync("PRIVATE_PROVIDER", user.Subject));
    }

    [Fact]
    public async Task ExplicitEnrollmentAcrossDistinctTenantsIsAllowedOnlyForUnambiguousCompanyIds()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var first = MemberDirectoryFixture.Authority();
        var second = MemberDirectoryFixture.Authority(); MemberDirectoryFixture.Seed(db, first); MemberDirectoryFixture.Seed(db, second);
        db.Users.Local.Single(row => row.Id == second.UserId).Subject = "PRIVATE_SUBJECT_" + first.UserId;
        await db.SaveChangesAsync();
        Assert.Equal(2, (await Directory(db).ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + first.UserId)).Count);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public async Task DirectoryNeverReturnsATruncatedOrOverlargeChoiceSet(int count, bool denied)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope); for (var index = 1; index < count; index++) AddCompany(db, scope.TenantId, scope.UserId, "Company " + index);
        await db.SaveChangesAsync();
        if (denied) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Directory(db).ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + scope.UserId));
        else Assert.Equal(count, (await Directory(db).ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + scope.UserId)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Name")]
    [InlineData("Name\nPRIVATE_DIAGNOSTIC")]
    public async Task MalformedNamesDenyWithoutPublishingPartialChoices(string name)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope); db.Companies.Local.Single().Name = name; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Directory(db).ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + scope.UserId));
    }

    [Fact]
    public async Task IsolatedSurrogatesCannotBeRepairedIntoPublishedCompanyNames()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope); var second = AddCompany(db, scope.TenantId, scope.UserId, "Valid second company");
        foreach (var name in new[] { "Name\ud800", "Name\udc00", "Name\ud800x", "Name\udc00\ud800" })
        {
            db.Companies.Local.Single(row => row.Id == second).Name = name; await db.SaveChangesAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Directory(db).ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + scope.UserId));
        }
        var valid = "Company \ud83d\ude00 \ufffd";
        db.Companies.Local.Single(row => row.Id == second).Name = valid; await db.SaveChangesAsync();
        Assert.Equal(valid, (await Directory(db).ListAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + scope.UserId)).Single(row => row.CompanyId == second).CompanyName);
    }

    [Fact]
    public async Task CancellationAndUnsupportedIdentityInputsFailBeforeDiscovery()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Directory(db).ListAsync("P", "S", cancelled.Token));
        foreach (var (provider, subject) in new[] { ("", "S"), ("P", " "), (new string('P', 101), "S"), ("P", new string('S', 201)) })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Directory(db).ListAsync(provider, subject));
    }

    private static EfAuthenticatedCompanyDirectory Directory(PlatformDbContext db) => new(db);
    private static Guid AddCompany(PlatformDbContext db, Guid tenant, Guid user, string name)
    {
        var id = Guid.NewGuid(); db.Companies.Add(new() { TenantId = tenant, Id = id, Code = id.ToString(), Name = name });
        db.CompanyMemberships.Add(new() { TenantId = tenant, CompanyId = id, UserId = user }); return id;
    }
}
