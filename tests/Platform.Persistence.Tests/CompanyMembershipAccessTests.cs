using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyMembershipAccessTests
{
    [Fact]
    public async Task SuspendAndReactivatePreserveIdentityRolesAndScopedAudit()
    {
        await using var fixture = await Fixture.Create();
        var originalUser = JsonSerializer.Serialize(await fixture.Db.Users.AsNoTracking().SingleAsync(row => row.Id == fixture.Target));
        var roles = await fixture.Db.RoleAssignments.AsNoTracking().OrderBy(row => row.UserId).ThenBy(row => row.RoleKey).Select(row => row.RoleKey).ToArrayAsync();
        var inactive = await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request);
        Assert.False(inactive.MembershipActive); Assert.Equal("2", inactive.MembershipVersion);
        Assert.Null(await new EfAuthorizationDirectory(fixture.Db).ResolveAsync(AuthorizationContext.Create(fixture.Scope.TenantId, fixture.Scope.CompanyId, fixture.Target)));
        var active = await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, new(Guid.NewGuid(), "2", true));
        Assert.True(active.MembershipActive); Assert.Equal("3", active.MembershipVersion);
        Assert.NotNull(await new EfAuthorizationDirectory(fixture.Db).ResolveAsync(AuthorizationContext.Create(fixture.Scope.TenantId, fixture.Scope.CompanyId, fixture.Target)));
        Assert.Equal(originalUser, JsonSerializer.Serialize(await fixture.Db.Users.AsNoTracking().SingleAsync(row => row.Id == fixture.Target)));
        Assert.Equal(roles, await fixture.Db.RoleAssignments.AsNoTracking().OrderBy(row => row.UserId).ThenBy(row => row.RoleKey).Select(row => row.RoleKey).ToArrayAsync());
        var audits = await fixture.Db.CompanyMembershipAccessAudits.AsNoTracking().OrderBy(row => row.BeforeVersion).ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.All(audits, audit => { Assert.Equal(fixture.Scope.TenantId, audit.TenantId); Assert.Equal(fixture.Scope.CompanyId, audit.CompanyId); Assert.Equal(fixture.Scope.UserId, audit.ActorUserId); Assert.Equal(fixture.Target, audit.TargetUserId); Assert.Equal(64, audit.RequestHash.Length); });
        Assert.True(audits[0].BeforeActive); Assert.False(audits[0].AfterActive); Assert.Equal(1, audits[0].BeforeVersion); Assert.Equal(2, audits[0].AfterVersion);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(inactive)); Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(audits));
    }
    [Fact]
    public async Task ExactRetryReturnsOriginalOutcomeWithoutReapplyingAfterLaterChange()
    {
        await using var fixture = await Fixture.Create();
        var first = await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request);
        Assert.Equal(first, await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request));
        await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, new(Guid.NewGuid(), "2", true));
        Assert.Equal(first, await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request));
        Assert.True((await fixture.Db.CompanyMemberships.AsNoTracking().SingleAsync(row => row.UserId == fixture.Target)).IsActive);
        Assert.Equal(2, await fixture.Db.CompanyMembershipAccessAudits.CountAsync());
        await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request with { IsActive = true }));
        await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Scope.UserId, fixture.Request));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    public async Task InvalidVersionHasNoEffect(string version)
    {
        await using var fixture = await Fixture.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request with { ExpectedVersion = version }));
        Assert.Empty(fixture.Db.CompanyMembershipAccessAudits); Assert.True((await fixture.Db.CompanyMemberships.AsNoTracking().SingleAsync(row => row.UserId == fixture.Target)).IsActive);
    }
    [Theory]
    [InlineData("viewer")]
    [InlineData("ADMIN")]
    [InlineData("inactive-user")]
    [InlineData("inactive-membership")]
    [InlineData("inactive-company")]
    public async Task FreshRevocationDeniesMutationAndRetry(string mode)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request);
        if (mode == "inactive-user") fixture.Db.Users.Single(row => row.Id == fixture.Scope.UserId).IsActive = false;
        if (mode == "inactive-membership") fixture.Db.CompanyMemberships.Single(row => row.UserId == fixture.Scope.UserId).IsActive = false;
        if (mode == "inactive-company") fixture.Db.Companies.Single().IsActive = false;
        // RoleKey is a key; remove/add instead of modifying a tracked key.
        if (mode is "viewer" or "ADMIN")
        {
            fixture.Db.ChangeTracker.Clear();
            fixture.Db.RoleAssignments.RemoveRange(await fixture.Db.RoleAssignments.Where(row => row.UserId == fixture.Scope.UserId).ToArrayAsync());
            fixture.Db.RoleAssignments.Add(new() { TenantId = fixture.Scope.TenantId, CompanyId = fixture.Scope.CompanyId, UserId = fixture.Scope.UserId, RoleKey = mode });
        }
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, new(Guid.NewGuid(), "2", true)));
        Assert.Single(fixture.Db.CompanyMembershipAccessAudits);
    }
    [Fact]
    public async Task SelfForeignStaleAndInactiveUserChangesFailWithoutAudit()
    {
        await using var fixture = await Fixture.Create();
        var self = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Scope.UserId, fixture.Request));
        Assert.Equal("self-deactivation", self.Code);
        await Assert.ThrowsAsync<CompanyMembershipNotFoundException>(() => fixture.Service.SetAccessAsync(fixture.Scope, Guid.NewGuid(), fixture.Request));
        var stale = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request with { ExpectedVersion = "2" }));
        Assert.Equal("stale-version", stale.Code);
        fixture.Db.Users.Single(row => row.Id == fixture.Target).IsActive = false; await fixture.Db.SaveChangesAsync();
        var inactive = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request with { IsActive = true }));
        Assert.Equal("inactive-user", inactive.Code); Assert.Empty(fixture.Db.CompanyMembershipAccessAudits);
    }
    [Fact]
    public async Task AuthorityRevokedBeforeSaveLeavesNoPendingRetryableMutation()
    {
        await using var fixture = await Fixture.Create();
        var service = new CompanyMembershipAccessService(fixture.Db, new ChangingDirectory(fixture.Scope));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request));
        Assert.False(fixture.Db.ChangeTracker.HasChanges()); Assert.Empty(fixture.Db.CompanyMembershipAccessAudits);
        Assert.True((await fixture.Db.CompanyMemberships.AsNoTracking().SingleAsync(row => row.UserId == fixture.Target)).IsActive);
    }
    private sealed class ChangingDirectory(AuthorizationContext scope) : IAuthorizationDirectory
    {
        private int calls;
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext authority, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthorizationDirectoryEntry?>(++calls < 3 ? new(scope, ["admin"]) : null);
    }
    [Fact]
    public async Task NoOpIsAuditedOnceAndCannotBypassVersionOrSelfFence()
    {
        await using var fixture = await Fixture.Create();
        var request = fixture.Request with { IsActive = true };
        var result = await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, request);
        Assert.True(result.MembershipActive); Assert.Equal("1", result.MembershipVersion);
        Assert.Equal(result, await fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target, request));
        var audit = Assert.Single(fixture.Db.CompanyMembershipAccessAudits);
        Assert.True(audit.BeforeActive); Assert.True(audit.AfterActive); Assert.Equal(audit.BeforeVersion, audit.AfterVersion);
    }
    [Fact]
    public async Task ForeignMembershipCannotBeChangedAndHugeVersionCannotWrap()
    {
        await using var fixture = await Fixture.Create();
        var foreign = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(fixture.Db, foreign); await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<CompanyMembershipNotFoundException>(() => fixture.Service.SetAccessAsync(fixture.Scope, foreign.UserId, fixture.Request));
        fixture.Db.CompanyMemberships.Single(row => row.UserId == fixture.Target).Version = long.MaxValue; await fixture.Db.SaveChangesAsync();
        var conflict = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => fixture.Service.SetAccessAsync(fixture.Scope, fixture.Target,
            fixture.Request with { ExpectedVersion = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        Assert.Equal("version-limit", conflict.Code); Assert.Empty(fixture.Db.CompanyMembershipAccessAudits);
    }
    [Fact]
    public async Task ExactLastAdministratorFenceIgnoresCaseAliasesAndInactiveAdmins()
    {
        await using var fixture = await Fixture.Create();
        fixture.Db.RoleAssignments.RemoveRange(fixture.Db.RoleAssignments.Where(row => row.UserId == fixture.Scope.UserId));
        fixture.Db.RoleAssignments.Add(new() { TenantId = fixture.Scope.TenantId, CompanyId = fixture.Scope.CompanyId, UserId = fixture.Scope.UserId, RoleKey = "ADMIN" });
        fixture.Db.RoleAssignments.Add(new() { TenantId = fixture.Scope.TenantId, CompanyId = fixture.Scope.CompanyId, UserId = fixture.Target, RoleKey = "admin" });
        MemberDirectoryFixture.AddMember(fixture.Db, fixture.Scope, Guid.NewGuid(), "Inactive admin", membershipActive: false, roles: ["admin"]);
        await fixture.Db.SaveChangesAsync();
        // Exercise the independent last-admin fence even if an injected
        // directory incorrectly supplies admin authority for a case alias.
        var service = new CompanyMembershipAccessService(fixture.Db, new ConstantDirectory(fixture.Scope));
        var conflict = await Assert.ThrowsAsync<CompanyMembershipAccessConflictException>(() => service.SetAccessAsync(fixture.Scope, fixture.Target, fixture.Request));
        Assert.Equal("last-administrator", conflict.Code); Assert.Empty(fixture.Db.CompanyMembershipAccessAudits);
    }
    [Fact]
    public async Task ModelHasScopedAuditKeysAndOptimisticMembershipConcurrency()
    {
        await using var fixture = await Fixture.Create();
        var member = fixture.Db.Model.FindEntityType(typeof(CompanyMembershipRecord))!;
        Assert.True(member.FindProperty(nameof(CompanyMembershipRecord.Version))!.IsConcurrencyToken);
        var audit = fixture.Db.Model.FindEntityType(typeof(CompanyMembershipAccessAuditRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "Id" }, audit.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(audit.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(new[] { "TenantId", "CompanyId", "OperationId" }));
        Assert.All(audit.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
    }
    private sealed class ConstantDirectory(AuthorizationContext scope) : IAuthorizationDirectory
    {
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext authority, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthorizationDirectoryEntry?>(new(scope, ["admin"]));
    }
    private sealed class Fixture(PlatformDbContext db, AuthorizationContext scope, Guid target) : IAsyncDisposable
    {
        public PlatformDbContext Db => db;
        public AuthorizationContext Scope => scope;
        public Guid Target => target;
        public CompanyMembershipAccessRequest Request { get; } = new(Guid.NewGuid(), "1", false);
        public CompanyMembershipAccessService Service { get; } = new(db, new EfAuthorizationDirectory(db));
        public static async Task<Fixture> Create()
        {
            var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var scope = MemberDirectoryFixture.Authority(); var target = Guid.NewGuid();
            MemberDirectoryFixture.Seed(db, scope); MemberDirectoryFixture.AddMember(db, scope, target, "Member", roles: ["viewer"]); await db.SaveChangesAsync();
            return new(db, scope, target);
        }
        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
