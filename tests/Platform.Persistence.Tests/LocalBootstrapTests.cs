extern alias Bootstrap;

using Microsoft.EntityFrameworkCore;
using Xunit;
using MinhHuy.AIOffice.Platform.Persistence;
using BootstrapOptions = Bootstrap::MinhHuy.AIOffice.Platform.Bootstrap.BootstrapOptions;
using LocalPlatformSeeder = Bootstrap::MinhHuy.AIOffice.Platform.Bootstrap.LocalPlatformSeeder;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class LocalBootstrapTests
{
    [Theory]
    [InlineData(null, "true")]
    [InlineData("Production", "true")]
    [InlineData("Staging", "true")]
    [InlineData("Development", null)]
    [InlineData("Development", "false")]
    public void BootstrapRefusesImplicitOrNonLocalEnvironments(string? environment, string? enabled)
    {
        Assert.Throws<InvalidOperationException>(() => BootstrapOptions.Read(name => name switch
        {
            "DOTNET_ENVIRONMENT" => environment,
            "AIOFFICE_LOCAL_BOOTSTRAP" => enabled,
            _ => null
        }));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("Aa1_abcdefghijklmnopqrstuvwxyz0123456789';DROP LOGIN sa;--")]
    [InlineData("Aa1_abcdefghijklmnopqrstuvwxyz0123456789\n")]
    public void BootstrapRefusesUnsafeGeneratedSecrets(string secret)
    {
        Assert.Throws<InvalidOperationException>(() => BootstrapOptions.Read(name => name switch
        {
            "DOTNET_ENVIRONMENT" => "Development",
            "AIOFFICE_LOCAL_BOOTSTRAP" => "true",
            _ when name.EndsWith("_ID", StringComparison.Ordinal) => Guid.NewGuid().ToString(),
            _ => secret
        }));
    }

    [Fact]
    public async Task RepeatSetupPreservesDisabledAccountsRolesAndEditedDataSources()
    {
        await using var db = Database();
        var options = Options();
        var seed = new LocalPlatformSeeder(db);
        await seed.SeedAsync(options, "verified-oidc-subject");
        var user = await db.Users.SingleAsync();
        user.IsActive = false;
        var source = await db.DataSources.SingleAsync();
        source.IsEnabled = false;
        source.LogicalName = "Customer configured source";
        db.RoleAssignments.RemoveRange(db.RoleAssignments);
        await db.SaveChangesAsync();

        await seed.SeedAsync(options, "verified-oidc-subject");

        Assert.False((await db.Users.SingleAsync()).IsActive);
        Assert.False((await db.DataSources.SingleAsync()).IsEnabled);
        Assert.Equal("Customer configured source", (await db.DataSources.SingleAsync()).LogicalName);
        Assert.Empty(db.RoleAssignments);
        Assert.Single(db.Companies);
        Assert.Single(db.PlatformMetadata);
    }

    [Fact]
    public async Task ChangedInstallationOrOidcSubjectIsRejectedWithoutReplacingUsers()
    {
        await using var db = Database();
        var options = Options();
        var seed = new LocalPlatformSeeder(db);
        await seed.SeedAsync(options, "original-subject");
        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.SeedAsync(
            options with { InstallationId = Guid.NewGuid() }, "original-subject"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.SeedAsync(options, "different-subject"));
        Assert.Equal("original-subject", (await db.Users.SingleAsync()).Subject);
        Assert.Single(db.CompanyMemberships);
    }

    [Fact]
    public async Task BootstrapRefusesPreexistingUnownedCompany()
    {
        await using var db = Database();
        var options = Options();
        db.Companies.Add(new CompanyRecord
        {
            TenantId = options.TenantId,
            Id = Guid.NewGuid(),
            Code = "EXISTING",
            Name = "Existing company"
        });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalPlatformSeeder(db)
            .SeedAsync(options, "new-subject"));
        Assert.Empty(db.Users);
        Assert.Empty(db.PlatformMetadata);
    }

    [Fact]
    public async Task FirstOwnerHasAuthoritativeCompanyAndReadOnlySource()
    {
        await using var db = Database();
        var options = Options();
        await new LocalPlatformSeeder(db).SeedAsync(options, "actual-keycloak-subject");
        var user = await db.Users.SingleAsync();
        Assert.Equal("local-keycloak", user.IdentityProvider);
        Assert.Equal("actual-keycloak-subject", user.Subject);
        Assert.Equal(options.UserId, user.Id);
        var source = await db.DataSources.SingleAsync();
        Assert.Equal(options.TenantId, source.TenantId);
        Assert.Equal(options.CompanyId, source.CompanyId);
        Assert.True(source.AllowRead);
        Assert.False(source.AllowWrite);
        Assert.Equal("secretref://env/PILOT_ERP_CONNECTION", source.ConnectionSecretReference);
    }

    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static BootstrapOptions Options() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), "Aa1_abcdefghijklmnopqrstuvwxyz0123456789", "Aa1_abcdefghijklmnopqrstuvwxyz0123456789",
        "Aa1_abcdefghijklmnopqrstuvwxyz0123456789", "Aa1_abcdefghijklmnopqrstuvwxyz0123456789", "Aa1_abcdefghijklmnopqrstuvwxyz0123456789");
}
