using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyInvitationProvisioningTests
{
    private static readonly MinhHuy.AIOffice.Shared.Contracts.AuthorizationContext Authority = MemberDirectoryFixture.Authority();
    [Fact]
    public async Task Creates_random_password_once_and_persists_only_outcome()
    {
        await using var database = await DatabaseAsync();
        var fake = new FakeKeycloak(); var logs = new CapturedLogs();
        var service = new CompanyInvitationService(database, new EfAuthorizationDirectory(database), fake, logs);
        var authority = Authority;
        var result = await service.CreateAsync(authority, new("Member@Example.com", ["viewer"]));
        Assert.Equal("member@example.com", fake.Email);
        Assert.Equal(fake.Password, result.TemporaryPassword);
        Assert.InRange(result.TemporaryPassword!.Length, 14, 128);
        Assert.Contains(database.CompanyInvitationAudits, row => row.Action == "keycloak-created");
        var stored = JsonSerializer.Serialize(database.ChangeTracker.Entries().Select(entry => entry.Entity));
        Assert.DoesNotContain(result.TemporaryPassword, stored);
        Assert.DoesNotContain(result.TemporaryPassword, result.ToString());
        Assert.DoesNotContain(result.TemporaryPassword, logs.Text);
        var listed = Assert.Single((await service.ListAsync(authority)).Items);
        Assert.Null(listed.TemporaryPassword);
        Assert.DoesNotContain("TemporaryPassword", JsonSerializer.Serialize(listed));
        await service.CreateAsync(authority, new("second@example.com", ["viewer"]));
        Assert.NotEqual(result.TemporaryPassword, fake.Password);
        var revoked = await service.RevokeAsync(authority, result.Id);
        Assert.Null(revoked.TemporaryPassword);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Existing_account_or_creation_race_never_returns_or_resets_password(bool exists, bool race)
    {
        await using var database = await DatabaseAsync();
        var fake = new FakeKeycloak { Exists = exists, Race = race };
        var result = await new CompanyInvitationService(database, new EfAuthorizationDirectory(database), fake)
            .CreateAsync(Authority, new("member@example.com", ["viewer"]));
        Assert.Null(result.TemporaryPassword);
        Assert.Equal(exists ? 0 : 1, fake.Creates);
        Assert.Contains(database.CompanyInvitationAudits, row => row.Action == "keycloak-existing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Keycloak_failure_leaves_no_invitation_or_audit(bool failCreation)
    {
        await using var database = await DatabaseAsync();
        var fake = new FakeKeycloak { FailLookup = !failCreation, FailCreate = failCreation };
        var logs = new CapturedLogs();
        var error = await Assert.ThrowsAsync<KeycloakProvisioningException>(() =>
            new CompanyInvitationService(database, new EfAuthorizationDirectory(database), fake, logs)
                .CreateAsync(Authority, new("member@example.com", ["viewer"])));
        Assert.Empty(database.CompanyInvitations); Assert.Empty(database.CompanyInvitationAudits);
        Assert.Empty(database.ChangeTracker.Entries());
        if (fake.Password is not null) { Assert.DoesNotContain(fake.Password, error.ToString()); Assert.DoesNotContain(fake.Password, logs.Text); }
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Duplicate_and_existing_member_conflicts_precede_keycloak_calls()
    {
        await using var database = await DatabaseAsync();
        var authority = Authority;
        await new CompanyInvitationService(database, new EfAuthorizationDirectory(database)).CreateAsync(authority, new("member@example.com", ["viewer"]));
        var fake = new FakeKeycloak();
        var service = new CompanyInvitationService(database, new EfAuthorizationDirectory(database), fake);
        await Assert.ThrowsAsync<CompanyInvitationConflictException>(() => service.CreateAsync(authority, new("member@example.com", ["viewer"])));
        var user = await database.Users.SingleAsync(row => row.Id == authority.UserId);
        user.NormalizedEmail = "admin@example.com"; await database.SaveChangesAsync();
        await Assert.ThrowsAsync<CompanyInvitationConflictException>(() => service.CreateAsync(authority, new("admin@example.com", ["viewer"])));
        Assert.Equal(0, fake.Lookups); Assert.Equal(0, fake.Creates);
    }

    [Fact]
    public async Task Disabled_feature_does_not_call_keycloak()
    {
        await using var database = await DatabaseAsync();
        var fake = new FakeKeycloak { Enabled = false, FailLookup = true };
        var result = await new CompanyInvitationService(database, new EfAuthorizationDirectory(database), fake)
            .CreateAsync(Authority, new("member@example.com", ["viewer"]));
        Assert.Null(result.TemporaryPassword); Assert.Equal(0, fake.Lookups); Assert.Equal(0, fake.Creates);
        Assert.Equal("created", Assert.Single(database.CompanyInvitationAudits).Action);
    }

    [Fact]
    public async Task Non_admin_is_denied_before_keycloak()
    {
        await using var database = await DatabaseAsync("viewer"); var fake = new FakeKeycloak();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CompanyInvitationService(database, new EfAuthorizationDirectory(database), fake)
            .CreateAsync(Authority, new("member@example.com", ["viewer"])));
        Assert.Equal(0, fake.Lookups); Assert.Empty(database.CompanyInvitations);
    }

    [Fact]
    public async Task Failed_commit_does_not_disclose_password_and_retry_preserves_account()
    {
        var failure = new FailSave();
        await using var database = await DatabaseAsync(interceptor: failure);
        var fake = new FakeKeycloak(); var logs = new CapturedLogs();
        var service = new CompanyInvitationService(database, new EfAuthorizationDirectory(database), fake, logs);
        failure.Enabled = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateAsync(Authority, new("member@example.com", ["viewer"])));
        Assert.Empty(database.CompanyInvitations); Assert.Empty(database.CompanyInvitationAudits);
        Assert.DoesNotContain(fake.Password!, logs.Text);
        failure.Enabled = false; fake.Exists = true;
        var retry = await service.CreateAsync(Authority, new("member@example.com", ["viewer"]));
        Assert.Null(retry.TemporaryPassword); Assert.Equal(1, fake.Creates);
    }

    private static async Task<PlatformDbContext> DatabaseAsync(string role = "admin", IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString());
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var database = new PlatformDbContext(options.Options);
        MemberDirectoryFixture.Seed(database, Authority, role);
        await database.SaveChangesAsync(); return database;
    }

    internal sealed class FakeKeycloak : IKeycloakAdminClient
    {
        public bool Enabled { get; set; } = true;
        public bool Exists { get; set; }
        public bool Race { get; set; }
        public bool FailLookup { get; set; }
        public bool FailCreate { get; set; }
        public int Lookups { get; private set; }
        public int Creates { get; private set; }
        public string? Email { get; private set; }
        public string? Password { get; private set; }
        public Task<bool> UserExistsAsync(string email, CancellationToken cancellationToken)
        { Lookups++; Email = email; if (FailLookup) throw new KeycloakProvisioningException(true); return Task.FromResult(Exists); }
        public Task<bool> CreateUserAsync(string email, string temporaryPassword, CancellationToken cancellationToken)
        { Creates++; Password = temporaryPassword; if (FailCreate) throw new KeycloakProvisioningException(); return Task.FromResult(!Race); }
    }
    private sealed class CapturedLogs : ILogger<CompanyInvitationService>
    {
        public string Text { get; private set; } = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Text += formatter(state, exception) + exception?.ToString();
    }
    private sealed class FailSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Enabled ? throw new DbUpdateException("Synthetic commit failure.") : ValueTask.FromResult(result);
    }
}
