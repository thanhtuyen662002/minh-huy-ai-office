extern alias RuntimeWorker;

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupIngressPipelineRegistrationTests
{
    private static readonly GroupExtractionWorkerBinding Worker = new(
        Guid.Parse("a0000000-0000-0000-0000-000000000001"),
        Guid.Parse("b0000000-0000-0000-0000-000000000001"),
        Guid.Parse("c0000000-0000-0000-0000-000000000001"), 7);

    private static Dictionary<string, string?> Settings() => new()
    {
        ["AIOffice:GroupIntake:Enabled"] = "true",
        ["AIOffice:GroupIntake:PipelineEnabled"] = "true",
        ["AIOffice:GroupIntake:Worker:TenantId"] = Worker.TenantId.ToString("D"),
        ["AIOffice:GroupIntake:Worker:CompanyId"] = Worker.CompanyId.ToString("D"),
        ["AIOffice:GroupIntake:Worker:ServiceId"] = Worker.ServiceId.ToString("D"),
        ["AIOffice:GroupIntake:Worker:CredentialEpoch"] = "7"
    };

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("True")]
    public void DefaultOffCreatesNoGroupDependencyOrResource(string? enabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        var original = services.ToArray();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AIOffice:GroupIntake:PipelineEnabled"] = enabled }).Build();
        Assert.False(services.AddGroupIngressReferenceProducer(config, false));
        Assert.False(services.AddGroupIngressReferenceConsumer(config, false));
        Assert.Equal(original, services.ToArray());
    }

    [Theory]
    [InlineData("Enabled", "false")]
    [InlineData("Worker:TenantId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("Worker:CompanyId", "b00000000000000000000000000000001")]
    [InlineData("Worker:ServiceId", "C0000000-0000-0000-0000-000000000001")]
    [InlineData("Worker:CredentialEpoch", "0")]
    [InlineData("Worker:CredentialEpoch", "+7")]
    [InlineData("Worker:CredentialEpoch", "07")]
    [InlineData("Worker:CredentialEpoch", "9223372036854775808")]
    [InlineData("Worker:TenantId", null)]
    public void InvalidTrustedHostConfigurationRefusesBeforeRegistration(string key, string? value)
    {
        var settings = Settings(); settings[$"AIOffice:GroupIntake:{key}"] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        foreach (var producer in new[] { true, false })
        {
            var services = new ServiceCollection();
            var error = Assert.Throws<InvalidOperationException>(() => Register(services, configuration, true, producer));
            Assert.Equal("Group reference pipeline configuration is not available.", error.Message);
            Assert.Empty(services);
        }
    }

    [Fact]
    public void EnabledWithoutDatabaseRefusesBeforeRegistration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddGroupIngressReferenceProducer(configuration, false));
        Assert.Throws<InvalidOperationException>(() => services.AddGroupIngressReferenceConsumer(configuration, false));
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EachRoleResolvesOnlyItsOwnGroupRuntimeWithFixedAuthority(bool producer)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddOptions<RabbitMqWorkOptions>();
        services.AddDbContext<PlatformDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        Assert.True(Register(services, configuration, true, producer));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Equal(Worker, provider.GetRequiredService<GroupExtractionWorkerBinding>());
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        Assert.Equal(producer ? typeof(GroupIngressReferenceOutboxHostedService) : typeof(RabbitMqGroupIngressConsumer), hosted.GetType());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(RabbitMqWorkConsumer));
        using var scope = provider.CreateScope();
        if (producer)
        {
            Assert.IsType<RabbitMqGroupIngressPublisher>(provider.GetRequiredService<IGroupIngressReferencePublisher>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<GroupIngressOutboxDispatcher>());
            Assert.Null(scope.ServiceProvider.GetService<GroupIngressInboxStore>());
        }
        else
        {
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<GroupIngressInboxStore>());
            Assert.Null(scope.ServiceProvider.GetService<GroupIngressOutboxDispatcher>());
            Assert.Null(provider.GetService<IGroupIngressReferencePublisher>());
        }
        // Hosted services are resolved, never started: no broker or SQL sockets.
    }

    [Fact]
    public void SameHostCanShareRolesButCannotReplaceAnExistingHostAuthority()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        var services = new ServiceCollection();
        Assert.True(services.AddGroupIngressReferenceProducer(configuration, true));
        Assert.True(services.AddGroupIngressReferenceConsumer(configuration, true));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(GroupExtractionWorkerBinding));
        var before = services.ToArray();
        var changed = Settings(); changed["AIOffice:GroupIntake:Worker:CompanyId"] = Guid.NewGuid().ToString("D");
        var other = new ConfigurationBuilder().AddInMemoryCollection(changed).Build();
        Assert.Throws<InvalidOperationException>(() => services.AddGroupIngressReferenceProducer(other, true));
        Assert.Equal(before, services.ToArray());
    }

    [Fact]
    public async Task FairCatalogIsScopedCurrentGrantDueAndInboxDrivenWithRestartWrap()
    {
        using var fixture = new GroupIngressOutboxDispatcherTests.Fixture();
        var database = fixture.Auth.Db; var now = fixture.Auth.Clock.Current; var worker = fixture.Worker;
        Assert.Equal(fixture.Auth.Binding.Id, Assert.Single(Due(database, worker, null, now)).Id);
        Assert.Empty(Due(database, worker with { CompanyId = Guid.NewGuid() }, null, now));
        Assert.Empty(Due(database, worker with { ServiceId = Guid.NewGuid() }, null, now));
        Assert.Empty(Due(database, worker, fixture.Auth.Binding.IdentityHash, now));
        Assert.Single(Due(database, worker, null, now)); // Fresh process/wrapped catalog still sees SQL backlog.
        var outbox = database.GroupIngressOutbox.Single(); outbox.AvailableAtUtc = now.AddSeconds(5); database.SaveChanges();
        Assert.Empty(Due(database, worker, null, now));
        Assert.Single(Due(database, worker, null, now.AddSeconds(5)));
        fixture.Auth.Grant.IsEnabled = false; database.SaveChanges();
        Assert.Empty(Due(database, worker, null, now.AddSeconds(5)));
        fixture.Auth.Grant.IsEnabled = true; outbox.AvailableAtUtc = now; database.SaveChanges();
        await fixture.Inbox.ReceiveAsync(new(1, fixture.Auth.Scope, outbox.Id,
            outbox.MessageId, outbox.Revision, outbox.CommittedSequence));
        Assert.Empty(Due(database, worker, null, now.AddDays(1)));
        await fixture.RequireNoPrivateOrCursorEffectsAsync();
    }

    [Fact]
    public void BoundedCatalogTranslatesToNativeSqlWithoutOpeningConnection()
    {
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1;Database=AIOffice_Inert_Query;Integrated Security=true;Encrypt=false;Connect Timeout=1").Options);
        var sql = Due(database, Worker, new string('A', 64), GroupServiceAuthenticatorTests.Fixture.Now)
            .Select(binding => new { binding.Id, binding.IdentityHash }).Take(32).ToQueryString();
        Assert.Contains("TOP(@", sql, StringComparison.Ordinal);
        Assert.Contains("[aioffice].[GroupIngressInbox]", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.False(database.Database.GetDbConnection().State == System.Data.ConnectionState.Open);
    }

    private static bool Register(IServiceCollection services, IConfiguration configuration, bool hasDatabase, bool producer) =>
        producer ? services.AddGroupIngressReferenceProducer(configuration, hasDatabase) : services.AddGroupIngressReferenceConsumer(configuration, hasDatabase);

    private static IOrderedQueryable<GroupBindingRecord> Due(PlatformDbContext database, GroupExtractionWorkerBinding worker, string? after, DateTimeOffset now) =>
        (IOrderedQueryable<GroupBindingRecord>)typeof(GroupIngressReferenceOutboxHostedService)
            .GetMethod("DueSources", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [database, worker, after, now])!;
}
