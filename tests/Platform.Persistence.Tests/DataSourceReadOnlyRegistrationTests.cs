using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class DataSourceReadOnlyRegistrationTests
{
    [Fact]
    public async Task RegistersScopedReadOnlySourceAndOneAuditWithoutReleasingReference()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request);
        Assert.Equal(fixture.Authority.TenantId, created.TenantId);
        Assert.Equal(fixture.Authority.CompanyId, created.CompanyId);
        Assert.Equal("sql-server", created.Kind);
        Assert.True(created.AllowRead);
        Assert.False(created.AllowWrite);
        Assert.True(created.IsEnabled);
        var source = await fixture.Db.DataSources.SingleAsync();
        Assert.Equal(fixture.Grant.CanonicalReference, source.ConnectionSecretReference);
        var audit = await fixture.Db.DataSourceRegistrationAudits.SingleAsync();
        Assert.Equal(created.Id, audit.DataSourceId);
        Assert.Equal(fixture.Authority.UserId, audit.ActorUserId);
        Assert.Equal(fixture.Grant.Id, audit.BindingId);
        Assert.Equal(fixture.Grant.Version, audit.BindingVersion);
        Assert.Equal(fixture.Request.OperationId, audit.OperationId);
        Assert.Equal(64, audit.RequestHash.Length);
        Assert.DoesNotContain("secretref://", JsonSerializer.Serialize(created));
        Assert.DoesNotContain("secretref://", JsonSerializer.Serialize(audit));
        Assert.Single(fixture.Db.DataSourceSecretBindings);
    }

    [Fact]
    public async Task LostResponseRetryReturnsSameSourceAndMismatchedRetryOrDuplicateNameConflicts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request);
        fixture.Db.ChangeTracker.Clear();
        var retry = await fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request with { LogicalName = "  Approved ERP  " });
        Assert.Equal(first, retry);
        await Assert.ThrowsAsync<DataSourceRegistrationConflictException>(() => fixture.Service.RegisterReadOnlyAsync(
            fixture.Authority, fixture.Request with { Purpose = "Different request" }));
        await Assert.ThrowsAsync<DataSourceRegistrationConflictException>(() => fixture.Service.RegisterReadOnlyAsync(
            fixture.Authority, fixture.Request with { OperationId = Guid.NewGuid() }));
        Assert.Single(fixture.Db.DataSources);
        Assert.Single(fixture.Db.DataSourceRegistrationAudits);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("member")]
    [InlineData("user")]
    [InlineData("company")]
    [InlineData("disabled")]
    [InlineData("version")]
    [InlineData("foreign")]
    [InlineData("infrastructure")]
    [InlineData("noncanonical")]
    public async Task RevokedOrForeignAuthorityCannotCreateOrRetry(string mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (mode == "role") fixture.Db.RoleAssignments.Remove(await fixture.Db.RoleAssignments.SingleAsync());
        if (mode == "member") (await fixture.Db.CompanyMemberships.SingleAsync()).IsActive = false;
        if (mode == "user") (await fixture.Db.Users.SingleAsync()).IsActive = false;
        if (mode == "company") (await fixture.Db.Companies.SingleAsync()).IsActive = false;
        if (mode == "disabled") fixture.Grant.IsEnabled = false;
        if (mode == "version") fixture.Grant.Version++;
        if (mode == "foreign")
        {
            fixture.Db.DataSourceSecretBindings.Remove(fixture.Grant);
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();
            fixture.Grant.CompanyId = Guid.NewGuid();
            fixture.Db.DataSourceSecretBindings.Add(fixture.Grant);
        }
        if (mode == "infrastructure") fixture.Grant.CanonicalReference = "secretref://env/AIOFFICE_DB_CONNECTION";
        if (mode == "noncanonical") fixture.Grant.CanonicalReference = "secretref://ENV/APPROVED_ERP";
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request));
        Assert.Empty(fixture.Db.DataSources);
        Assert.Empty(fixture.Db.DataSourceRegistrationAudits);
    }

    [Fact]
    public async Task ExistingOperationCannotBypassRevokedGrantOrAdministration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request);
        fixture.Grant.IsEnabled = false;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request));
        Assert.Single(fixture.Db.DataSources);
        Assert.Single(fixture.Db.DataSourceRegistrationAudits);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    public async Task VersionRequiresCanonicalPositiveInt64Token(string token)
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RegisterReadOnlyAsync(fixture.Authority,
            fixture.Request with { BindingVersion = token }));
        Assert.Empty(fixture.Db.DataSources);
    }

    [Fact]
    public async Task MaximumInt64VersionSurvivesOptionsAndRegistrationExactly()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Grant.Version = long.MaxValue;
        await fixture.Db.SaveChangesAsync();
        var option = Assert.Single((await fixture.Service.ListRegistrationOptionsAsync(fixture.Authority)).Items);
        Assert.Equal("9223372036854775807", option.VersionToken);
        await fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request with { BindingVersion = option.VersionToken });
        Assert.Equal(long.MaxValue, (await fixture.Db.DataSourceRegistrationAudits.SingleAsync()).BindingVersion);
    }

    [Fact]
    public async Task AuthorityLossBeforeSaveLeavesNoSourceOrAuditAndDetachesPendingRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new DataSourceRegistryService(fixture.Db, new RevokingDirectory(fixture.Authority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request));
        Assert.Empty(fixture.Db.DataSources);
        Assert.Empty(fixture.Db.DataSourceRegistrationAudits);
        Assert.DoesNotContain(fixture.Db.ChangeTracker.Entries(), row => row.State == EntityState.Added);
    }

    [Fact]
    public async Task SaveFailureDoesNotRetainPendingRowsAndCallerCancellationIsPreserved()
    {
        var failure = new FailRegistrationSave();
        await using var fixture = await Fixture.CreateAsync(failure);
        failure.Enabled = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request));
        Assert.Empty(fixture.Db.DataSources);
        Assert.Empty(fixture.Db.DataSourceRegistrationAudits);
        Assert.DoesNotContain(fixture.Db.ChangeTracker.Entries(), row => row.State == EntityState.Added);
        failure.Enabled = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request, cancellation.Token));
        await fixture.Service.RegisterReadOnlyAsync(fixture.Authority, fixture.Request);
        Assert.Single(fixture.Db.DataSources);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task AuditPrivilegeProofFailsClosed(int proof, bool denied)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(proof);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        var verifier = new RegistrationAuditPermissionVerifier(db);
        if (denied) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => verifier.RequireAppendOnlyAsync());
        else await verifier.RequireAppendOnlyAsync();
        Assert.Equal(1, connection.Calls);
    }

    private sealed class RevokingDirectory(AuthorizationContext authority) : IAuthorizationDirectory
    {
        private int calls;
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthorizationDirectoryEntry?>(++calls > 2 ? null : new(authority, ["admin"]));
    }

    private sealed class FailRegistrationSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) throw new DbUpdateException("Synthetic persistence failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required PlatformDbContext Db { get; init; }
        public required AuthorizationContext Authority { get; init; }
        public required DataSourceSecretBindingRecord Grant { get; init; }
        public DataSourceRegistryService Service => new(Db, new EfAuthorizationDirectory(Db));
        public DataSourceReadOnlyRegistrationRequest Request => new(Grant.Id, "1", operationId, "Approved ERP", "Production", "Business reporting", 2);
        private readonly Guid operationId = Guid.NewGuid();

        public static async Task<Fixture> CreateAsync(SaveChangesInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase("registration-" + Guid.NewGuid());
            if (interceptor is not null) options.AddInterceptors(interceptor);
            var db = new PlatformDbContext(options.Options);
            var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            db.Users.Add(new() { TenantId = authority.TenantId, Id = authority.UserId, IdentityProvider = "fixture", Subject = "owner", DisplayName = "Owner", IsActive = true });
            db.Companies.Add(new() { TenantId = authority.TenantId, Id = authority.CompanyId, Code = "FIXTURE", Name = "Company", IsActive = true });
            db.CompanyMemberships.Add(new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, UserId = authority.UserId, IsActive = true });
            db.RoleAssignments.Add(new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, UserId = authority.UserId, RoleKey = "admin" });
            var grant = BindingFixture.Grant(db, authority, "secretref://env/APPROVED_ERP");
            await db.SaveChangesAsync();
            return new() { Db = db, Authority = authority, Grant = grant };
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
