using System.Globalization;
using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyAdministratorTests
{
    [Fact]
    public async Task ExactRawRoleQueryTranslatesBeforeOpeningAnyConnection()
    {
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=QueryTranslationOnly;Integrated Security=true")
            .AddInterceptors(new StopBeforeConnection()).Options);
        var authority = MemberDirectoryFixture.Authority();
        var service = new CompanyAdministratorService(database, new ConstantDirectory(authority));
        var query = typeof(CompanyAdministratorService).GetMethod("ReadAssignmentsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<RoleAssignmentRecord[]>)query.Invoke(service, [authority, Guid.NewGuid(), CancellationToken.None])!;
        await Assert.ThrowsAsync<QueryTranslated>(() => task);
    }

    private sealed class QueryTranslated : Exception;
    private sealed class StopBeforeConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw new QueryTranslated();
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    [InlineData(1)]
    public async Task MalformedStoredRoleCannotBeRepairedInAudit(int codepoint)
    {
        await using var f = await Fixture.Create();
        f.Db.RoleAssignments.Add(new() { TenantId = f.Scope.TenantId, CompanyId = f.Scope.CompanyId, UserId = f.Target, RoleKey = "role" + (char)codepoint });
        await f.Db.SaveChangesAsync();
        var denied = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request));
        Assert.Equal("invalid-role-state", denied.Code); Assert.Empty(f.Db.CompanyAdministratorAudits);
    }

    [Fact]
    public async Task GrantRemoveAndReplayPreserveIdentityOtherRolesAndOriginalOutcome()
    {
        await using var f = await Fixture.Create();
        var userBefore = JsonSerializer.Serialize(await f.Db.Users.AsNoTracking().SingleAsync(row => row.Id == f.Target));
        var grant = await f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request);
        Assert.True(grant.IsAdministrator); Assert.Equal("2", grant.MembershipVersion);
        Assert.Equal(grant, await f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request));
        Assert.Contains("admin", (await new EfAuthorizationDirectory(f.Db).ResolveAsync(f.TargetScope))!.Roles);
        var remove = await f.Service.SetAdministratorAsync(f.Scope, f.Target, new(Guid.NewGuid(), "2", false));
        Assert.False(remove.IsAdministrator); Assert.Equal("3", remove.MembershipVersion);
        Assert.Equal(grant, await f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request));
        Assert.Equal(new[] { "erp-reader", "viewer" }, (await new EfAuthorizationDirectory(f.Db).ResolveAsync(f.TargetScope))!.Roles.Order(StringComparer.Ordinal));
        Assert.True((await f.Db.CompanyMemberships.AsNoTracking().SingleAsync(row => row.UserId == f.Target)).IsActive);
        Assert.Equal(userBefore, JsonSerializer.Serialize(await f.Db.Users.AsNoTracking().SingleAsync(row => row.Id == f.Target)));
        var audits = await f.Db.CompanyAdministratorAudits.AsNoTracking().OrderBy(row => row.BeforeVersion).ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.All(audits, row => { Assert.Equal(f.Scope.TenantId, row.TenantId); Assert.Equal(f.Scope.CompanyId, row.CompanyId); Assert.Equal(f.Scope.UserId, row.ActorUserId); Assert.Equal(f.Target, row.TargetUserId); Assert.Equal(64, row.RequestHash.Length); });
        Assert.Equal(new[] { "erp-reader", "viewer" }, JsonSerializer.Deserialize<string[]>(audits[0].BeforeRolesJson));
        Assert.Equal(new[] { "admin", "erp-reader", "viewer" }, JsonSerializer.Deserialize<string[]>(audits[0].AfterRolesJson));
        Assert.Equal(audits[0].AfterRolesJson, audits[1].BeforeRolesJson); Assert.False(audits[1].AfterAdministrator);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(audits));
    }

    [Fact]
    public async Task NoOpAuditedOnceButStaleAndAlteredReplayCannotBypassFences()
    {
        await using var f = await Fixture.Create();
        var noOp = f.Request with { IsAdministrator = false };
        var result = await f.Service.SetAdministratorAsync(f.Scope, f.Target, noOp);
        Assert.Equal("1", result.MembershipVersion); Assert.Equal(result, await f.Service.SetAdministratorAsync(f.Scope, f.Target, noOp));
        Assert.Single(f.Db.CompanyAdministratorAudits);
        await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, noOp with { IsAdministrator = true }));
        await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, new(Guid.NewGuid(), "2", false)));
        await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Scope.UserId, new(Guid.NewGuid(), "1", true)));
        Assert.Single(f.Db.CompanyAdministratorAudits);
    }

    [Fact]
    public async Task AccessAndRoleChangesShareOneConcurrencyToken()
    {
        await using var f = await Fixture.Create();
        await f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request);
        var access = new CompanyMembershipAccessService(f.Db, new EfAuthorizationDirectory(f.Db));
        await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => access.SetAccessAsync(f.Scope, f.Target, new(Guid.NewGuid(), "1", false)));
        await access.SetAccessAsync(f.Scope, f.Target, new(Guid.NewGuid(), "2", false));
        await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, new(Guid.NewGuid(), "2", false)));
        var inactive = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, new(Guid.NewGuid(), "3", false)));
        Assert.Equal("inactive-membership", inactive.Code);
        await access.SetAccessAsync(f.Scope, f.Target, new(Guid.NewGuid(), "3", true));
        var removed = await f.Service.SetAdministratorAsync(f.Scope, f.Target, new(Guid.NewGuid(), "4", false));
        Assert.Equal("5", removed.MembershipVersion); Assert.Equal(2, await f.Db.CompanyAdministratorAudits.CountAsync());
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("ADMIN")]
    [InlineData("admin ")]
    [InlineData("inactive-user")]
    [InlineData("inactive-membership")]
    [InlineData("inactive-company")]
    public async Task RevokedActorCannotMutateOrReplay(string mode)
    {
        await using var f = await Fixture.Create(); await f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request);
        if (mode == "inactive-user") f.Db.Users.Single(row => row.Id == f.Scope.UserId).IsActive = false;
        else if (mode == "inactive-membership") f.Db.CompanyMemberships.Single(row => row.UserId == f.Scope.UserId).IsActive = false;
        else if (mode == "inactive-company") f.Db.Companies.Single().IsActive = false;
        else
        {
            f.Db.RoleAssignments.RemoveRange(await f.Db.RoleAssignments.Where(row => row.UserId == f.Scope.UserId).ToArrayAsync());
            f.Db.RoleAssignments.Add(new() { TenantId = f.Scope.TenantId, CompanyId = f.Scope.CompanyId, UserId = f.Scope.UserId, RoleKey = mode });
        }
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, new(Guid.NewGuid(), "2", false)));
        Assert.Single(f.Db.CompanyAdministratorAudits);
    }

    [Fact]
    public async Task RevocationBeforeSaveLeavesNoTrackedEffects()
    {
        await using var f = await Fixture.Create();
        var service = new CompanyAdministratorService(f.Db, new ChangingDirectory(f.Scope));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetAdministratorAsync(f.Scope, f.Target, f.Request));
        Assert.False(f.Db.ChangeTracker.HasChanges()); Assert.Empty(f.Db.CompanyAdministratorAudits);
        Assert.DoesNotContain("admin", (await new EfAuthorizationDirectory(f.Db).ResolveAsync(f.TargetScope))!.Roles);
        Assert.Equal(1, (await f.Db.CompanyMemberships.AsNoTracking().SingleAsync(row => row.UserId == f.Target)).Version);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    public async Task InvalidVersionHasNoEffect(string version)
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request with { ExpectedVersion = version }));
        Assert.Empty(f.Db.CompanyAdministratorAudits);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("admin ")]
    [InlineData(" admin")]
    public async Task AmbiguousTargetRoleStateRefusedWithoutNormalizing(string alias)
    {
        await using var f = await Fixture.Create();
        f.Db.RoleAssignments.Add(new() { TenantId = f.Scope.TenantId, CompanyId = f.Scope.CompanyId, UserId = f.Target, RoleKey = alias });
        await f.Db.SaveChangesAsync();
        var denial = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request));
        Assert.Equal("invalid-role-state", denial.Code); Assert.Empty(f.Db.CompanyAdministratorAudits);
    }

    [Fact]
    public async Task ForeignInactiveSelfAndOverflowTargetsAreRefused()
    {
        await using var f = await Fixture.Create(); var foreign = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(f.Db, foreign); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<CompanyMembershipNotFoundException>(() => f.Service.SetAdministratorAsync(f.Scope, foreign.UserId, f.Request));
        var self = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Scope.UserId, f.Request)); Assert.Equal("self-role-change", self.Code);
        f.Db.Users.Single(row => row.Id == f.Target).IsActive = false; await f.Db.SaveChangesAsync();
        var inactive = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request)); Assert.Equal("inactive-user", inactive.Code);
        f.Db.Users.Single(row => row.Id == f.Target).IsActive = true;
        f.Db.CompanyMemberships.Single(row => row.UserId == f.Target).Version = long.MaxValue; await f.Db.SaveChangesAsync();
        var overflow = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target,
            f.Request with { ExpectedVersion = long.MaxValue.ToString(CultureInfo.InvariantCulture) }));
        Assert.Equal("version-limit", overflow.Code); Assert.Empty(f.Db.CompanyAdministratorAudits);
    }

    [Fact]
    public async Task LastAdministratorFenceIndependentlyIgnoresAliasAndInactiveMembers()
    {
        await using var f = await Fixture.Create();
        f.Db.RoleAssignments.RemoveRange(await f.Db.RoleAssignments.Where(row => row.UserId == f.Scope.UserId).ToArrayAsync());
        f.Db.RoleAssignments.Add(new() { TenantId = f.Scope.TenantId, CompanyId = f.Scope.CompanyId, UserId = f.Scope.UserId, RoleKey = "ADMIN" });
        f.Db.RoleAssignments.Add(new() { TenantId = f.Scope.TenantId, CompanyId = f.Scope.CompanyId, UserId = f.Target, RoleKey = "admin" });
        MemberDirectoryFixture.AddMember(f.Db, f.Scope, Guid.NewGuid(), "Inactive", membershipActive: false, roles: ["admin"]);
        await f.Db.SaveChangesAsync();
        var service = new CompanyAdministratorService(f.Db, new ConstantDirectory(f.Scope));
        var denial = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => service.SetAdministratorAsync(f.Scope, f.Target, f.Request with { IsAdministrator = false }));
        Assert.Equal("last-administrator", denial.Code); Assert.Empty(f.Db.CompanyAdministratorAudits);
    }

    [Fact]
    public async Task TooManyRolesAndUnrelatedPendingChangesAreNotCommitted()
    {
        await using var f = await Fixture.Create();
        for (var i = 0; i < 254; i++) f.Db.RoleAssignments.Add(new() { TenantId = f.Scope.TenantId, CompanyId = f.Scope.CompanyId, UserId = f.Target, RoleKey = "role" + i });
        await f.Db.SaveChangesAsync();
        var overflow = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request)); Assert.Equal("role-limit", overflow.Code);
        f.Db.Users.Single(row => row.Id == f.Target).DisplayName = "Pending";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request));
        Assert.Empty(f.Db.CompanyAdministratorAudits); Assert.True(f.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task CancellationEmptyIdsAndWrongReplayActorNeverChangeAuthority()
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request, new(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SetAdministratorAsync(f.Scope, Guid.Empty, f.Request));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request with { OperationId = Guid.Empty }));
        await f.Service.SetAdministratorAsync(f.Scope, f.Target, f.Request);
        var actor = Guid.NewGuid(); MemberDirectoryFixture.AddMember(f.Db, f.Scope, actor, "Second admin", roles: ["admin"]); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => f.Service.SetAdministratorAsync(AuthorizationContext.Create(f.Scope.TenantId, f.Scope.CompanyId, actor), f.Target, f.Request));
        Assert.Single(f.Db.CompanyAdministratorAudits);
    }

    private sealed class ChangingDirectory(AuthorizationContext scope) : IAuthorizationDirectory
    {
        private int calls;
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthorizationDirectoryEntry?>(++calls < 3 ? new(scope, ["admin"]) : null);
    }
    private sealed class ConstantDirectory(AuthorizationContext scope) : IAuthorizationDirectory
    {
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthorizationDirectoryEntry?>(new(scope, ["admin"]));
    }
    private sealed class Fixture(PlatformDbContext db, AuthorizationContext scope, Guid target) : IAsyncDisposable
    {
        public PlatformDbContext Db => db;
        public AuthorizationContext Scope => scope;
        public Guid Target => target;
        public AuthorizationContext TargetScope => AuthorizationContext.Create(scope.TenantId, scope.CompanyId, target);
        public CompanyAdministratorRequest Request { get; } = new(Guid.NewGuid(), "1", true);
        public CompanyAdministratorService Service { get; } = new(db, new EfAuthorizationDirectory(db));
        public static async Task<Fixture> Create()
        {
            var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority(); var target = Guid.NewGuid();
            MemberDirectoryFixture.Seed(db, scope); MemberDirectoryFixture.AddMember(db, scope, target, "Member", roles: ["viewer", "erp-reader"]); await db.SaveChangesAsync();
            return new(db, scope, target);
        }
        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
