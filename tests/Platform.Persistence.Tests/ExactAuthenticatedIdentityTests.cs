using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ExactAuthenticatedIdentityTests
{
    [Theory]
    [InlineData("OpaqueProvider", "CaseSensitiveSubject")]
    [InlineData("OpaqueProvider ", "CaseSensitiveSubject ")]
    [InlineData("OpaqueProvider", "IDENTITY-\u0130-\u0131")]
    public async Task ExactOpaqueKeysPreserveCharactersAndReturnOnlyScopedAuthority(string provider, string subject)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var authority = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, authority); var user = db.Users.Local.Single(); user.IdentityProvider = provider; user.Subject = subject; await db.SaveChangesAsync();
        var result = await new EfAuthenticatedAuthorizationDirectory(db).ResolveAsync(provider, subject, authority.CompanyId);
        Assert.NotNull(result); Assert.Equal(authority, result.Context); Assert.Equal(new[] { "admin" }, result.Roles);
    }
    [Theory]
    [InlineData("opaqueProvider", "CaseSensitiveSubject")]
    [InlineData("OpaqueProvider", "casesensitivesubject")]
    [InlineData("OpaqueProvider ", "CaseSensitiveSubject")]
    [InlineData("OpaqueProvider", "CaseSensitiveSubject ")]
    public async Task DifferentOpaqueKeysNeverGrantStoredIdentity(string provider, string subject)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var authority = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, authority); var user = db.Users.Local.Single(); user.IdentityProvider = "OpaqueProvider"; user.Subject = "CaseSensitiveSubject"; await db.SaveChangesAsync();
        Assert.Null(await new EfAuthenticatedAuthorizationDirectory(db).ResolveAsync(provider, subject, authority.CompanyId));
    }
    [Theory]
    [InlineData(101, 20)]
    [InlineData(20, 201)]
    public async Task UnsupportedIdentityLengthsCannotGrantAuthorityEvenWithSyntheticStoredRows(int providerLength, int subjectLength)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var authority = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, authority); var user = db.Users.Local.Single(); user.IdentityProvider = new string('P', providerLength); user.Subject = new string('S', subjectLength); await db.SaveChangesAsync();
        Assert.Null(await new EfAuthenticatedAuthorizationDirectory(db).ResolveAsync(user.IdentityProvider, user.Subject, authority.CompanyId));
    }
    [Fact]
    public async Task ExactIdentityStillDeniesAmbiguousTenantCompanyMappings()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var first = MemberDirectoryFixture.Authority();
        var second = MinhHuy.AIOffice.Shared.Contracts.AuthorizationContext.Create(Guid.NewGuid(), first.CompanyId, first.UserId);
        MemberDirectoryFixture.Seed(db, first); MemberDirectoryFixture.Seed(db, second); await db.SaveChangesAsync();
        Assert.Null(await new EfAuthenticatedAuthorizationDirectory(db).ResolveAsync("PRIVATE_PROVIDER", "PRIVATE_SUBJECT_" + first.UserId, first.CompanyId));
    }
}
