using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class DataSourceRegistrationOptionsTests
{
    [Fact]
    public async Task OptionsAreScopedCanonicalEnabledMetadataWithoutReferences()
    {
        await using var db = Context();
        var authority = await SeedAsync(db);
        var accepted = BindingFixture.Grant(db, authority, "secretref://env/APPROVED_ERP");
        accepted.Label = "  Approved ERP  ";
        accepted.Version = 7;
        BindingFixture.Grant(db, Guid.NewGuid(), authority.CompanyId, "secretref://env/FOREIGN_TENANT");
        BindingFixture.Grant(db, authority.TenantId, Guid.NewGuid(), "secretref://env/FOREIGN_COMPANY");
        BindingFixture.Grant(db, authority, "secretref://env/AIOFFICE_DB_CONNECTION");
        BindingFixture.Grant(db, authority, "secretref://env/CUSTOM_PLATFORM_DB");
        BindingFixture.Grant(db, authority, "secretref://env/DISABLED").IsEnabled = false;
        BindingFixture.Grant(db, authority, "secretref://env/INVALID_VERSION").Version = 0;
        BindingFixture.Grant(db, authority, "not-a-reference");
        BindingFixture.Grant(db, authority, "secretref://ENV/NONCANONICAL");
        BindingFixture.Grant(db, authority, "secretref://env/EMPTY_LABEL").Label = " ";
        BindingFixture.Grant(db, authority, "secretref://env/LONG_LABEL").Label = new string('x', 129);
        BindingFixture.Grant(db, authority, "secretref://env/REFERENCE_LABEL").Label = "Display secretref://env/PRIVATE_LABEL";
        await db.SaveChangesAsync();
        var service = new DataSourceSecretBindingService(db, new EfAuthorizationDirectory(db), infrastructureReference: "secretref://env/CUSTOM_PLATFORM_DB");
        var page = await service.ListRegistrationOptionsAsync(authority);
        var option = Assert.Single(page.Items);
        Assert.Equal(new DataSourceRegistrationOption(accepted.Id, "Approved ERP", 7), option);
        Assert.False(page.HasMore);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("secretref://", json);
        Assert.DoesNotContain("CanonicalReference", json);
        Assert.DoesNotContain("Connection", json);
        Assert.DoesNotContain("FOREIGN", json);
    }

    [Fact]
    public async Task PagingCountsOnlySafeChoicesAndNeverWritesGrants()
    {
        await using var db = Context();
        var authority = await SeedAsync(db);
        var first = BindingFixture.Grant(db, authority, "secretref://env/FIRST");
        first.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var invalid = BindingFixture.Grant(db, authority, "invalid");
        invalid.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var last = BindingFixture.Grant(db, authority, "secretref://env/LAST");
        last.Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        await db.SaveChangesAsync();
        var service = new DataSourceSecretBindingService(db, new EfAuthorizationDirectory(db));
        var page1 = await service.ListRegistrationOptionsAsync(authority, limit: 1);
        var page2 = await service.ListRegistrationOptionsAsync(authority, offset: 1, limit: 1);
        Assert.Equal(first.Id, Assert.Single(page1.Items).BindingId);
        Assert.True(page1.HasMore);
        Assert.Equal(last.Id, Assert.Single(page2.Items).BindingId);
        Assert.False(page2.HasMore);
        Assert.Empty((await service.ListRegistrationOptionsAsync(authority, offset: 2, limit: 1)).Items);
        Assert.Equal(3, await db.DataSourceSecretBindings.CountAsync());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State is EntityState.Modified or EntityState.Added or EntityState.Deleted);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("membership")]
    [InlineData("user")]
    [InlineData("company")]
    public async Task ReusedServiceCannotRetainRevokedAdministration(string revoke)
    {
        await using var db = Context();
        var authority = await SeedAsync(db);
        BindingFixture.Grant(db, authority, "secretref://env/APPROVED");
        await db.SaveChangesAsync();
        var service = new DataSourceSecretBindingService(db, new EfAuthorizationDirectory(db));
        Assert.Single((await service.ListRegistrationOptionsAsync(authority)).Items);
        if (revoke == "role") db.RoleAssignments.Remove(await db.RoleAssignments.SingleAsync());
        if (revoke == "membership") (await db.CompanyMemberships.SingleAsync()).IsActive = false;
        if (revoke == "user") (await db.Users.SingleAsync()).IsActive = false;
        if (revoke == "company") (await db.Companies.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRegistrationOptionsAsync(authority));
    }

    [Fact]
    public async Task RevokedBindingDisappearsOnTheNextRequest()
    {
        await using var db = Context();
        var authority = await SeedAsync(db);
        var grant = BindingFixture.Grant(db, authority, "secretref://env/APPROVED");
        await db.SaveChangesAsync();
        var service = new DataSourceSecretBindingService(db, new EfAuthorizationDirectory(db));
        Assert.Single((await service.ListRegistrationOptionsAsync(authority)).Items);
        grant.IsEnabled = false;
        grant.Version++;
        await db.SaveChangesAsync();
        Assert.Empty((await service.ListRegistrationOptionsAsync(authority)).Items);
    }

    [Fact]
    public async Task RevocationDuringQueryCannotReleaseMetadata()
    {
        await using var db = Context();
        var authority = await SeedAsync(db);
        BindingFixture.Grant(db, authority, "secretref://env/APPROVED");
        await db.SaveChangesAsync();
        var service = new DataSourceSecretBindingService(db, new ChangingDirectory(authority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRegistrationOptionsAsync(authority));
    }

    [Fact]
    public async Task UnsafeRelationalStoreFailsBeforeBindingRowsAreRead()
    {
        var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        var service = new DataSourceSecretBindingService(db, new ChangingDirectory(authority));
        var denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRegistrationOptionsAsync(authority));
        Assert.Equal("Data source is unavailable for authorized use.", denied.Message);
        Assert.Null(denied.InnerException);
        Assert.Equal(1, connection.Calls);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1001, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task InvalidPageBoundsAreRejected(int offset, int limit)
    {
        await using var db = Context();
        var authority = await SeedAsync(db);
        var service = new DataSourceSecretBindingService(db, new EfAuthorizationDirectory(db));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListRegistrationOptionsAsync(authority, offset, limit));
    }

    [Fact]
    public async Task CompanySnapshotOverflowFailsClosedInsteadOfInventingPagination()
    {
        await using var db = Context();
        var authority = await SeedAsync(db);
        for (var index = 0; index < 1001; index++) BindingFixture.Grant(db, authority, "secretref://env/BOUND_" + index);
        await db.SaveChangesAsync();
        var service = new DataSourceSecretBindingService(db, new EfAuthorizationDirectory(db));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRegistrationOptionsAsync(authority));
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        await using var db = Context();
        var authority = await SeedAsync(db);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new DataSourceSecretBindingService(db, new EfAuthorizationDirectory(db));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListRegistrationOptionsAsync(authority, cancellationToken: cancellation.Token));
    }

    private static PlatformDbContext Context() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseInMemoryDatabase("binding-options-" + Guid.NewGuid()).Options);

    private static async Task<AuthorizationContext> SeedAsync(PlatformDbContext db)
    {
        var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        db.Users.Add(new PlatformUserRecord { TenantId = authority.TenantId, Id = authority.UserId, IdentityProvider = "fixture", Subject = "fixture-user", DisplayName = "Owner", IsActive = true });
        db.Companies.Add(new CompanyRecord { TenantId = authority.TenantId, Id = authority.CompanyId, Code = "FIXTURE", Name = "Company", IsActive = true });
        db.CompanyMemberships.Add(new CompanyMembershipRecord { TenantId = authority.TenantId, CompanyId = authority.CompanyId, UserId = authority.UserId, IsActive = true });
        db.RoleAssignments.Add(new RoleAssignmentRecord { TenantId = authority.TenantId, CompanyId = authority.CompanyId, UserId = authority.UserId, RoleKey = "admin" });
        await db.SaveChangesAsync();
        return authority;
    }

    private sealed class ChangingDirectory(AuthorizationContext authority) : IAuthorizationDirectory
    {
        private bool revoked;
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
        {
            var entry = revoked ? null : new AuthorizationDirectoryEntry(authority, ["admin"]);
            revoked = true;
            return Task.FromResult(entry);
        }
    }
}
