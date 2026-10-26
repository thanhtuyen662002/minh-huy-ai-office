using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyInvitationTests
{
    [Fact]
    public async Task AdministratorCreatesListsAndDuplicateOrExistingMemberConflicts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(fixture.Authority, new(" MEMBER@EXAMPLE.COM ", ["viewer"]));
        Assert.Equal("member@example.com", created.Email);
        Assert.Equal(["viewer"], created.Roles);
        var listed = Assert.Single((await fixture.Service.ListAsync(fixture.Authority)).Items);
        Assert.Equal(created.Id, listed.Id);
        Assert.Equal(created.Email, listed.Email);
        Assert.Equal(created.Roles, listed.Roles);
        var duplicate = await Assert.ThrowsAsync<CompanyInvitationConflictException>(() =>
            fixture.Service.CreateAsync(fixture.Authority, new("member@example.com", ["viewer"])));
        Assert.Equal("duplicate-invitation", duplicate.Code);

        var existing = Guid.NewGuid();
        MemberDirectoryFixture.AddMember(fixture.Database, fixture.Authority, existing, "Existing", roles: ["viewer"]);
        fixture.Database.Users.Local.Single(row => row.Id == existing).NormalizedEmail = "existing@example.com";
        await fixture.Database.SaveChangesAsync();
        var member = await Assert.ThrowsAsync<CompanyInvitationConflictException>(() =>
            fixture.Service.CreateAsync(fixture.Authority, new("existing@example.com", ["viewer"])));
        Assert.Equal("existing-member", member.Code);
        Assert.Single(fixture.Database.CompanyInvitationAudits);
    }

    [Fact]
    public async Task ExpiredInvitationIsClosedBeforeReplacementAndRevokedInvitationCannotBeAccepted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.CreateAsync(fixture.Authority, new("expire@example.com", ["viewer"]));
        var stored = await fixture.Database.CompanyInvitations.SingleAsync(row => row.Id == first.Id);
        stored.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Database.SaveChangesAsync();
        var replacement = await fixture.Service.CreateAsync(fixture.Authority, new("expire@example.com", ["admin"]));
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.NotNull((await fixture.Database.CompanyInvitations.SingleAsync(row => row.Id == first.Id)).RevokedAtUtc);

        var revoked = await fixture.Service.RevokeAsync(fixture.Authority, replacement.Id);
        Assert.NotNull(revoked.RevokedAtUtc);
        var replay = await fixture.Service.RevokeAsync(fixture.Authority, replacement.Id);
        Assert.Equal(revoked.Id, replay.Id);
        Assert.Equal(revoked.Version, replay.Version);
        await fixture.Service.AcceptAsync("oidc", "revoked-subject", "expire@example.com", true);
        Assert.DoesNotContain(fixture.Database.Users, row => row.Subject == "revoked-subject");
        Assert.Equal(4, await fixture.Database.CompanyInvitationAudits.CountAsync());
    }

    [Fact]
    public async Task UnverifiedOrWrongEmailIsIgnoredAndVerifiedEmailAcceptsOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invitation = await fixture.Service.CreateAsync(fixture.Authority, new("member@example.com", ["admin", "viewer"]));
        await fixture.Service.AcceptAsync("oidc", "new-subject", "member@example.com", false);
        await fixture.Service.AcceptAsync("oidc", "new-subject", "wrong@example.com", true);
        Assert.DoesNotContain(fixture.Database.Users, row => row.Subject == "new-subject");

        await fixture.Service.AcceptAsync("oidc", "new-subject", "MEMBER@example.com", true);
        await fixture.Service.AcceptAsync("oidc", "new-subject", "member@example.com", true);
        var user = Assert.Single(fixture.Database.Users.Where(row => row.Subject == "new-subject"));
        Assert.Equal("member@example.com", user.NormalizedEmail);
        Assert.Single(fixture.Database.CompanyMemberships.Where(row => row.UserId == user.Id));
        Assert.Equal(["admin", "viewer"], fixture.Database.RoleAssignments.Where(row => row.UserId == user.Id)
            .OrderBy(row => row.RoleKey).Select(row => row.RoleKey).ToArray());
        var accepted = await fixture.Database.CompanyInvitations.SingleAsync(row => row.Id == invitation.Id);
        Assert.NotNull(accepted.AcceptedAtUtc);
        Assert.Equal(2, accepted.Version);
        Assert.Equal(["created", "accepted"], await fixture.Database.CompanyInvitationAudits
            .OrderBy(row => row.InvitationVersion).Select(row => row.Action).ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentFirstLoginsCreateOneIdentityMembershipAndRoleSet()
    {
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(databaseName).Options;
        var authority = MemberDirectoryFixture.Authority();
        await using (var seed = new PlatformDbContext(options))
        {
            MemberDirectoryFixture.Seed(seed, authority);
            await seed.SaveChangesAsync();
            await new CompanyInvitationService(seed, new EfAuthorizationDirectory(seed)).CreateAsync(authority,
                new("race@example.com", ["viewer"]));
        }
        await using var first = new PlatformDbContext(options);
        await using var second = new PlatformDbContext(options);
        await Task.WhenAll(
            new CompanyInvitationService(first, new EfAuthorizationDirectory(first)).AcceptAsync("oidc", "race-subject", "race@example.com", true),
            new CompanyInvitationService(second, new EfAuthorizationDirectory(second)).AcceptAsync("oidc", "race-subject", "race@example.com", true));
        await using var check = new PlatformDbContext(options);
        var user = Assert.Single(await check.Users.Where(row => row.Subject == "race-subject").ToArrayAsync());
        Assert.Single(await check.CompanyMemberships.Where(row => row.UserId == user.Id).ToArrayAsync());
        Assert.Single(await check.RoleAssignments.Where(row => row.UserId == user.Id && row.RoleKey == "viewer").ToArrayAsync());
        Assert.Equal(2, await check.CompanyInvitationAudits.CountAsync());
    }

    [Fact]
    public async Task NonAdministratorAndForeignCompanyCannotReadOrMutateInvitations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invitation = await fixture.Service.CreateAsync(fixture.Authority, new("member@example.com", ["viewer"]));
        var member = AuthorizationContext.Create(fixture.Authority.TenantId, fixture.Authority.CompanyId, Guid.NewGuid());
        MemberDirectoryFixture.AddMember(fixture.Database, fixture.Authority, member.UserId, "Member", roles: ["viewer"]);
        var foreign = AuthorizationContext.Create(fixture.Authority.TenantId, Guid.NewGuid(), fixture.Authority.UserId);
        fixture.Database.Companies.Add(new() { TenantId = foreign.TenantId, Id = foreign.CompanyId, Code = "FOREIGN", Name = "Foreign" });
        fixture.Database.CompanyMemberships.Add(new() { TenantId = foreign.TenantId, CompanyId = foreign.CompanyId, UserId = foreign.UserId });
        fixture.Database.RoleAssignments.Add(new() { TenantId = foreign.TenantId, CompanyId = foreign.CompanyId, UserId = foreign.UserId, RoleKey = "admin" });
        await fixture.Database.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ListAsync(member));
        await Assert.ThrowsAsync<CompanyMembershipNotFoundException>(() => fixture.Service.RevokeAsync(foreign, invitation.Id));
        Assert.Null((await fixture.Database.CompanyInvitations.SingleAsync()).RevokedAtUtc);
    }

    [Fact]
    public async Task BootstrapIsIdempotentAndRefusesChangedCompanyOrEmail()
    {
        await using var database = new PlatformDbContext(MemberDirectoryFixture.Options());
        var service = new CompanyInvitationService(database, new EfAuthorizationDirectory(database));
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        var first = await service.BootstrapAsync(tenant, company, "pilot", "Pilot", "admin@example.com");
        var replay = await service.BootstrapAsync(tenant, company, "pilot", "Pilot", "ADMIN@example.com");
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Email, replay.Email);
        Assert.Equal(first.Roles, replay.Roles);
        var conflict = await Assert.ThrowsAsync<CompanyInvitationConflictException>(() =>
            service.BootstrapAsync(tenant, company, "pilot", "Pilot", "other@example.com"));
        Assert.Equal("bootstrap-conflict", conflict.Code);
        Assert.Single(database.Companies);
        Assert.Single(database.CompanyInvitations);
        Assert.Single(database.CompanyInvitationAudits);
    }

    private sealed class Fixture(PlatformDbContext database, AuthorizationContext authority) : IAsyncDisposable
    {
        public PlatformDbContext Database => database;
        public AuthorizationContext Authority => authority;
        public CompanyInvitationService Service { get; } = new(database, new EfAuthorizationDirectory(database));
        public static async Task<Fixture> CreateAsync()
        {
            var database = new PlatformDbContext(MemberDirectoryFixture.Options());
            var authority = MemberDirectoryFixture.Authority();
            MemberDirectoryFixture.Seed(database, authority);
            await database.SaveChangesAsync();
            return new(database, authority);
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
}
