using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ScopedDataSourceSecretBindingTests
{
    [Theory]
    [InlineData("env", "AIOFFICE_DB_CONNECTION", null, true)]
    [InlineData("env", "%2FAIOFFICE_DB_CONNECTION", null, true)]
    [InlineData("env", "aioffice_db_connection", null, false)]
    [InlineData("env", "CUSTOM_PLATFORM_KEY", "secretref://env/CUSTOM_PLATFORM_KEY", true)]
    [InlineData("env", "custom_platform_key", "secretref://env/CUSTOM_PLATFORM_KEY", false)]
    [InlineData("env", "Custom_Platform_Key", "secretref://ENV/CUSTOM_PLATFORM_KEY", false)]
    [InlineData("test", "PLATFORM_KEY", "secretref://test/PLATFORM_KEY", true)]
    [InlineData("test", "platform_key", "secretref://test/PLATFORM_KEY", false)]
    [InlineData("env", "CUSTOMER_KEY", "secretref://test/CUSTOMER_KEY", false)]
    [InlineData("env", "CUSTOMER_KEY", "secretref://env/OTHER_PLATFORM_KEY", false)]
    public async Task Infrastructure_exclusion_uses_provider_and_OS_semantics_only(
        string provider, string resource, string? platformReference, bool exactProtected)
    {
        await using var fixture = new Fixture();
        fixture.Source.ConnectionSecretReference = $"secretref://{provider}/{resource}";
        fixture.Grant.CanonicalReference = SecretReference.Parse(fixture.Source.ConnectionSecretReference).Value;
        await fixture.SeedAsync();
        var resolver = new ProviderRecordingResolver(provider);
        var scoped = new ScopedDataSourceSecretResolver(new(fixture.Db, new EfAuthorizationDirectory(fixture.Db),
            infrastructureReference: platformReference), new CompositeSecretResolver([resolver]));
        var denied = exactProtected || (OperatingSystem.IsWindows() && provider == "env"
            && (resource.Equals("AIOFFICE_DB_CONNECTION", StringComparison.OrdinalIgnoreCase)
                || resource.Equals("CUSTOM_PLATFORM_KEY", StringComparison.OrdinalIgnoreCase)));
        var uses = 0;
        Task<int> Use() => scoped.UseAsync(fixture.Authority, fixture.Source.Id, true,
            (_, _) => { uses++; return Task.FromResult(7); });
        if (denied) await Assert.ThrowsAsync<UnauthorizedAccessException>(Use);
        else Assert.Equal(7, await Use());
        Assert.Equal(denied ? 0 : 1, resolver.Calls);
        Assert.Equal(denied ? 0 : 1, uses);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("ambiguous")]
    [InlineData("foreign-company")]
    [InlineData("foreign-tenant")]
    [InlineData("inactive-user")]
    [InlineData("inactive-company")]
    [InlineData("inactive-membership")]
    [InlineData("disabled-source")]
    [InlineData("write-enabled")]
    [InlineData("different-case")]
    [InlineData("platform-reference")]
    [InlineData("configured-platform-reference")]
    public async Task Unavailable_authority_never_calls_resolver_or_use(string condition)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        switch (condition)
        {
            case "missing": fixture.Db.Remove(fixture.Grant); break;
            case "revoked": fixture.Grant.IsEnabled = false; break;
            case "ambiguous": BindingFixture.Grant(fixture.Db, fixture.Authority, Fixture.Reference); break;
            case "foreign-company":
                fixture.Db.Remove(fixture.Grant); BindingFixture.Grant(fixture.Db,
                AuthorizationContext.Create(fixture.Authority.TenantId, Guid.NewGuid(), fixture.Authority.UserId), Fixture.Reference); break;
            case "foreign-tenant":
                fixture.Db.Remove(fixture.Grant); BindingFixture.Grant(fixture.Db,
                AuthorizationContext.Create(Guid.NewGuid(), fixture.Authority.CompanyId, fixture.Authority.UserId), Fixture.Reference); break;
            case "inactive-user": (await fixture.Db.Users.SingleAsync()).IsActive = false; break;
            case "inactive-company": (await fixture.Db.Companies.SingleAsync()).IsActive = false; break;
            case "inactive-membership": (await fixture.Db.CompanyMemberships.SingleAsync()).IsActive = false; break;
            case "disabled-source": fixture.Source.IsEnabled = false; break;
            case "write-enabled": fixture.Source.AllowWrite = true; break;
            case "different-case": fixture.Grant.CanonicalReference = Fixture.Reference.ToLowerInvariant(); break;
            case "platform-reference": fixture.Grant.CanonicalReference = fixture.Source.ConnectionSecretReference = "secretref://env/AIOFFICE_DB_CONNECTION"; break;
            case "configured-platform-reference": break;
        }
        await fixture.Db.SaveChangesAsync();
        var service = new DataSourceSecretBindingService(fixture.Db, new EfAuthorizationDirectory(fixture.Db),
            infrastructureReference: condition == "configured-platform-reference" ? Fixture.Reference : null);
        var resolver = new RecordingResolver();
        var scoped = new ScopedDataSourceSecretResolver(service, new CompositeSecretResolver([resolver]));
        var uses = 0;
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scoped.UseAsync(
            fixture.Authority, fixture.Source.Id, true, (_, _) => { uses++; return Task.FromResult(1); }));
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, uses);
        Assert.Equal("Data source is unavailable for authorized use.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("version")]
    [InlineData("recreate")]
    [InlineData("membership")]
    [InlineData("user")]
    [InlineData("company")]
    [InlineData("source")]
    [InlineData("reference")]
    [InlineData("task-owner")]
    public async Task Change_during_secret_resolution_is_rechecked_before_connection_use(string change)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var resolver = new RecordingResolver(async () =>
        {
            await using var writer = new PlatformDbContext(fixture.Options);
            switch (change)
            {
                case "grant": (await writer.DataSourceSecretBindings.SingleAsync()).IsEnabled = false; break;
                case "version": (await writer.DataSourceSecretBindings.SingleAsync()).Version++; break;
                case "recreate":
                    writer.Remove(await writer.DataSourceSecretBindings.SingleAsync());
                    await writer.SaveChangesAsync(); BindingFixture.Grant(writer, fixture.Authority, Fixture.Reference); break;
                case "membership": (await writer.CompanyMemberships.SingleAsync()).IsActive = false; break;
                case "user": (await writer.Users.SingleAsync()).IsActive = false; break;
                case "company": (await writer.Companies.SingleAsync()).IsActive = false; break;
                case "source": (await writer.DataSources.SingleAsync()).IsEnabled = false; break;
                case "reference": (await writer.DataSources.SingleAsync()).ConnectionSecretReference = "secretref://test/OTHER"; break;
                case "task-owner": (await writer.Tasks.SingleAsync()).CreatedByUserId = Guid.NewGuid(); break;
            }
            await writer.SaveChangesAsync();
        });
        var scoped = new ScopedDataSourceSecretResolver(new(fixture.Db, new EfAuthorizationDirectory(fixture.Db)),
            new CompositeSecretResolver([resolver]));
        var uses = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scoped.UseAsync(fixture.Authority,
            fixture.Source.Id, true, (_, _) => { uses++; return Task.FromResult(1); }, taskId: change == "task-owner" ? fixture.TaskId : null));
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, uses);
    }

    [Fact]
    public async Task Explicit_grant_is_shared_and_revalidation_never_uses_a_tracked_grant()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var scoped = new ScopedDataSourceSecretResolver(new(fixture.Db, new EfAuthorizationDirectory(fixture.Db)),
            new CompositeSecretResolver([new RecordingResolver()]));
        Assert.Equal(7, await scoped.UseAsync(fixture.Authority, fixture.Source.Id, true, (_, _) => Task.FromResult(7)));
        await using var writer = new PlatformDbContext(fixture.Options);
        (await writer.DataSourceSecretBindings.SingleAsync()).IsEnabled = false;
        await writer.SaveChangesAsync();
        Assert.True(fixture.Grant.IsEnabled); // Deliberately stale tracked entity.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scoped.UseAsync(fixture.Authority,
            fixture.Source.Id, true, (_, _) => Task.FromResult(7)));
    }

    [Fact]
    public async Task Missing_or_unavailable_durable_task_owner_fails_closed()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var service = new DataSourceSecretBindingService(fixture.Db, new EfAuthorizationDirectory(fixture.Db));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TaskAuthorityAsync(
            fixture.Authority.TenantId, fixture.Authority.CompanyId, Guid.NewGuid()));
        var unavailable = new DataSourceSecretBindingService(fixture.Db, new UnavailableDirectory());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unavailable.RequireSourceAsync(
            fixture.Authority, fixture.Source.Id, true));
    }

    private sealed class UnavailableDirectory : IAuthorizationDirectory
    {
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("synthetic storage fault");
    }

    private sealed class RecordingResolver(Func<Task>? duringResolve = null) : ISecretResolver
    {
        public string Provider => "test";
        public int Calls { get; private set; }
        public async ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (duringResolve is not null) await duringResolve();
            return "synthetic-only-value";
        }
    }

    private sealed class ProviderRecordingResolver(string provider) : ISecretResolver
    {
        public string Provider => provider;
        public int Calls { get; private set; }
        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult("synthetic-only-value");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Reference = "secretref://test/OWNED_ERP";
        public DbContextOptions<PlatformDbContext> Options { get; } = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public PlatformDbContext Db { get; }
        public AuthorizationContext Authority { get; } = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        public DataSourceRecord Source { get; }
        public DataSourceSecretBindingRecord Grant { get; }
        public Guid TaskId { get; } = Guid.NewGuid();
        public Fixture()
        {
            Db = new(Options);
            Db.Add(new PlatformUserRecord
            {
                TenantId = Authority.TenantId,
                Id = Authority.UserId,
                IdentityProvider = "test",
                Subject = "fixture-user",
                DisplayName = "Fixture",
                IsActive = true
            });
            Db.Add(new CompanyRecord
            {
                TenantId = Authority.TenantId,
                Id = Authority.CompanyId,
                Code = "FIXTURE",
                Name = "Fixture",
                IsActive = true
            });
            Db.Add(new CompanyMembershipRecord
            {
                TenantId = Authority.TenantId,
                CompanyId = Authority.CompanyId,
                UserId = Authority.UserId,
                IsActive = true
            });
            Source = new()
            {
                TenantId = Authority.TenantId,
                CompanyId = Authority.CompanyId,
                Id = Guid.NewGuid(),
                LogicalName = "fixture",
                Kind = "erp",
                Environment = "test",
                Purpose = "synthetic",
                ConnectionSecretReference = Reference,
                IsEnabled = true,
                AllowRead = true,
                AllowWrite = false,
                MaxConcurrency = 1
            };
            Db.Add(Source);
            Db.Add(new TaskRecord
            {
                TenantId = Authority.TenantId,
                CompanyId = Authority.CompanyId,
                Id = TaskId,
                CreatedByUserId = Authority.UserId
            });
            Grant = BindingFixture.Grant(Db, Authority, Reference);
        }
        public Task SeedAsync() => Db.SaveChangesAsync();
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}

internal static class BindingFixture
{
    // Only explicit synthetic fixtures; shipping bootstrap/operator paths own grants.
    public static DataSourceSecretBindingRecord Grant(PlatformDbContext db, AuthorizationContext authority, string reference)
        => Grant(db, authority.TenantId, authority.CompanyId, reference);

    public static DataSourceSecretBindingRecord Grant(PlatformDbContext db, Guid tenantId, Guid companyId, string reference)
    {
        var grant = new DataSourceSecretBindingRecord
        {
            TenantId = tenantId,
            CompanyId = companyId,
            Id = Guid.NewGuid(),
            CanonicalReference = reference,
            Label = "Synthetic explicit grant",
            IsEnabled = true,
            Version = 1
        };
        db.Add(grant);
        return grant;
    }
}
