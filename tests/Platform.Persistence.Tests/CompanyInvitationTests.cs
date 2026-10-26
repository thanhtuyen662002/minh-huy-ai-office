using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyInvitationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingIdentitySignInBypassesMutationGateAndPreservesTracker(bool pendingChanges)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync(fixture.Authority, new("member@example.com", ["viewer"]));
        var user = await fixture.Database.Users.AsNoTracking().SingleAsync(row => row.Id == fixture.Authority.UserId);
        fixture.Database.ChangeTracker.Clear();
        var company = await fixture.Database.Companies.SingleAsync();
        if (pendingChanges) fixture.Database.Entry(company).Property(row => row.Name).CurrentValue = "Pending edit";
        var expectedState = pendingChanges ? EntityState.Modified : EntityState.Unchanged;
        var trackerEvents = 0;
        fixture.Database.ChangeTracker.Tracked += (_, _) => trackerEvents++;
        fixture.Database.ChangeTracker.StateChanged += (_, _) => trackerEvents++;

        await WhileMutationGateIsHeldAsync(token => fixture.Service.AcceptAsync(
            user.IdentityProvider, user.Subject, "member@example.com", true, token));

        var entry = Assert.Single(fixture.Database.ChangeTracker.Entries());
        Assert.Same(company, entry.Entity);
        Assert.Equal(expectedState, entry.State);
        Assert.Equal(0, trackerEvents);
        Assert.Null((await fixture.Database.CompanyInvitations.AsNoTracking().SingleAsync()).AcceptedAtUtc);
        Assert.Single(await fixture.Database.Users.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Database.CompanyInvitationAudits.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("absent", false)]
    [InlineData("absent", true)]
    [InlineData("expired", false)]
    [InlineData("revoked", false)]
    [InlineData("accepted", false)]
    [InlineData("other-email", false)]
    public async Task SignInWithoutCandidateInvitationBypassesMutationGateAndPreservesTracker(string state, bool pendingChanges)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (state != "absent")
        {
            await fixture.Service.CreateAsync(fixture.Authority, new("member@example.com", ["viewer"]));
            var invitation = await fixture.Database.CompanyInvitations.SingleAsync();
            if (state == "expired") invitation.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (state == "revoked") invitation.RevokedAtUtc = DateTimeOffset.UtcNow;
            if (state == "accepted") invitation.AcceptedAtUtc = DateTimeOffset.UtcNow;
            await fixture.Database.SaveChangesAsync();
        }
        fixture.Database.ChangeTracker.Clear();
        var company = await fixture.Database.Companies.SingleAsync();
        if (pendingChanges) fixture.Database.Entry(company).Property(row => row.Name).CurrentValue = "Pending edit";
        var expectedState = pendingChanges ? EntityState.Modified : EntityState.Unchanged;
        var trackerEvents = 0;
        fixture.Database.ChangeTracker.Tracked += (_, _) => trackerEvents++;
        fixture.Database.ChangeTracker.StateChanged += (_, _) => trackerEvents++;

        await WhileMutationGateIsHeldAsync(token => fixture.Service.AcceptAsync("oidc", "new-subject",
            state == "other-email" ? "other@example.com" : " MEMBER@EXAMPLE.COM ", true, token));

        var entry = Assert.Single(fixture.Database.ChangeTracker.Entries());
        Assert.Same(company, entry.Entity);
        Assert.Equal(expectedState, entry.State);
        Assert.Equal(pendingChanges ? "Pending edit" : "Company", company.Name);
        Assert.Equal(0, trackerEvents);
        Assert.DoesNotContain(await fixture.Database.Users.AsNoTracking().ToArrayAsync(), row => row.Subject == "new-subject");
        Assert.Equal(state == "absent" ? 0 : 1, await fixture.Database.CompanyInvitationAudits.CountAsync());
    }

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
        Task firstLogin = Task.CompletedTask, secondLogin = Task.CompletedTask;
        await WhileMutationGateIsHeldAsync(token =>
        {
            firstLogin = new CompanyInvitationService(first, new EfAuthorizationDirectory(first))
                .AcceptAsync("oidc", "race-subject", "race@example.com", true, token);
            secondLogin = new CompanyInvitationService(second, new EfAuthorizationDirectory(second))
                .AcceptAsync("oidc", "race-subject", "race@example.com", true, token);
            Assert.False(firstLogin.IsCompleted);
            Assert.False(secondLogin.IsCompleted);
            return Task.CompletedTask;
        });
        await Task.WhenAll(firstLogin, secondLogin);
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

    private static async Task WhileMutationGateIsHeldAsync(Func<CancellationToken, Task> signIn)
    {
        var barrier = new InvitationSaveBarrier();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(barrier).Options;
        await using var blocker = new PlatformDbContext(options);
        var mutation = new CompanyInvitationService(blocker, new EfAuthorizationDirectory(blocker))
            .BootstrapAsync(Guid.NewGuid(), Guid.NewGuid(), "blocker", "Blocker", "blocker@example.com");
        try
        {
            // Bootstrap holds the same mutation gate used by acceptance until its save completes.
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await signIn(timeout.Token);
            Assert.False(mutation.IsCompleted);
        }
        finally
        {
            barrier.Release.TrySetResult();
            await mutation;
        }
    }

    private sealed class InvitationSaveBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
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
