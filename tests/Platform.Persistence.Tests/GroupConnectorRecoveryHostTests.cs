extern alias RuntimeWorker;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupConnectorRecoveryHostTests
{
    private const string Prefix = "AIOffice:GroupIntake:Connector:";
    private static readonly Guid Tenant = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Company = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Service = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Source = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static Dictionary<string, string?> Settings(string root) => new()
    {
        ["AIOffice:GroupIntake:Enabled"] = "true",
        ["AIOffice:GroupIntake:ConnectorRecoveryEnabled"] = "true",
        [Prefix + "TenantId"] = Tenant.ToString("D"),
        [Prefix + "CompanyId"] = Company.ToString("D"),
        [Prefix + "ServiceId"] = Service.ToString("D"),
        [Prefix + "ConnectorAccountId"] = Guid.NewGuid().ToString("D"),
        [Prefix + "CredentialEpoch"] = "7",
        [Prefix + "BackendOrigin"] = "https://owned.invalid/",
        [Prefix + "PrivateRoot"] = root,
        [Prefix + "SigningSecretRef"] = "secretref://env/OWNED_SIGNING",
        [Prefix + "SpoolSecretRef"] = "secretref://env/OWNED_SPOOL",
        [Prefix + "SpoolKeyId"] = "spool-v1",
        [Prefix + "Provider"] = "owned-provider",
        [Prefix + "ExternalAccountId"] = "owned-account",
        [Prefix + "Sources:0:SourceBindingId"] = Source.ToString("D"),
        [Prefix + "Sources:0:ExternalGroupId"] = "owned-group"
    };
    private static string MissingRoot() => Path.Combine(Path.GetTempPath(), "aioffice-inert-recovery-" + Guid.NewGuid().ToString("N"));
    private static IConfigurationRoot Configuration(Dictionary<string, string?> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("True")]
    [InlineData(" true")]
    public void DefaultOffDoesNotRegisterOrReadMalformedConfiguration(string? enabled)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AIOffice:GroupIntake:ConnectorRecoveryEnabled"] = enabled,
            [Prefix + "SigningSecretRef"] = "PRIVATE_INVALID",
            [Prefix + "PrivateRoot"] = "PRIVATE_INVALID"
        };
        var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(TimeProvider.System); var original = services.ToArray();
        Assert.False(services.AddGroupConnectorRecovery(Configuration(settings), "Production", false));
        Assert.Equal(original, services.ToArray());
    }

    [Theory]
    [InlineData("TenantId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("CompanyId", "b00000000000000000000000000000001")]
    [InlineData("ServiceId", "C0000000-0000-0000-0000-000000000001")]
    [InlineData("ConnectorAccountId", null)]
    [InlineData("CredentialEpoch", "0")]
    [InlineData("CredentialEpoch", "+7")]
    [InlineData("CredentialEpoch", "07")]
    [InlineData("CredentialEpoch", "9223372036854775808")]
    [InlineData("BackendOrigin", "http://127.0.0.1/")]
    [InlineData("BackendOrigin", "https://private:secret@owned.invalid/")]
    [InlineData("BackendOrigin", "https://owned.invalid/private")]
    [InlineData("BackendOrigin", "https://owned.invalid/?private=yes")]
    [InlineData("BackendOrigin", "https://owned.invalid/#private")]
    [InlineData("PrivateRoot", "relative/private")]
    [InlineData("SigningSecretRef", "PRIVATE_PLAINTEXT")]
    [InlineData("SpoolSecretRef", "secretref://ENV/owned_signing")]
    [InlineData("SpoolKeyId", "private/key")]
    [InlineData("Provider", null)]
    [InlineData("ExternalAccountId", "")]
    [InlineData("Sources:0:SourceBindingId", "PRIVATE_INVALID")]
    [InlineData("Sources:0:ExternalGroupId", null)]
    public void InvalidHostConfigurationRefusesBeforeAnyRegistrationOrResource(string key, string? value)
    {
        var root = MissingRoot(); var settings = Settings(root); settings[Prefix + key] = value; var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddGroupConnectorRecovery(Configuration(settings), "Production", true));
        Assert.Equal("Group connector recovery configuration is not available.", error.Message);
        Assert.Empty(services); Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EnabledNeedsPlatformAndGroupReadiness(bool database, bool group)
    {
        var settings = Settings(MissingRoot()); settings["AIOffice:GroupIntake:Enabled"] = group ? "true" : "false";
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddGroupConnectorRecovery(Configuration(settings), "Production", database)); Assert.Empty(services);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("duplicate-source")]
    [InlineData("duplicate-group")]
    [InlineData("root")]
    [InlineData("overflow")]
    public void HostRejectsAmbiguousEnrollmentAndUnboundedOrRootStorage(string changed)
    {
        var root = MissingRoot(); var settings = Settings(root);
        if (changed == "root") settings[Prefix + "PrivateRoot"] = Path.GetPathRoot(root);
        else if (changed == "gap") { settings[Prefix + "Sources:2:SourceBindingId"] = Guid.NewGuid().ToString("D"); settings[Prefix + "Sources:2:ExternalGroupId"] = "other"; }
        else if (changed == "overflow")
            for (var index = 1; index <= 256; index++) { settings[Prefix + $"Sources:{index}:SourceBindingId"] = Guid.NewGuid().ToString("D"); settings[Prefix + $"Sources:{index}:ExternalGroupId"] = "other" + index; }
        else
        {
            settings[Prefix + "Sources:1:SourceBindingId"] = changed == "duplicate-source" ? Source.ToString("D") : Guid.NewGuid().ToString("D");
            settings[Prefix + "Sources:1:ExternalGroupId"] = changed == "duplicate-group" ? "owned-group" : "other";
        }
        var services = new ServiceCollection(); Assert.Throws<InvalidOperationException>(() => services.AddGroupConnectorRecovery(Configuration(settings), "Production", true));
        Assert.Empty(services); Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("Production", true, false)]
    [InlineData("Development", false, false)]
    [InlineData("Development", true, true)]
    public void LoopbackSyntheticProfileRequiresOwnedDevelopmentFixture(string environment, bool owned, bool allowed)
    {
        var root = MissingRoot(); var settings = Settings(root); settings["AIOffice:GroupIntake:OwnedSyntheticFixture"] = "true";
        settings["AIOffice:GroupIntake:OwnedDisposableFixture"] = owned ? "true" : "false"; settings[Prefix + "BackendOrigin"] = "http://127.0.0.1/";
        var services = new ServiceCollection();
        if (allowed) Assert.True(services.AddGroupConnectorRecovery(Configuration(settings), environment, true));
        else { Assert.Throws<InvalidOperationException>(() => services.AddGroupConnectorRecovery(Configuration(settings), environment, true)); Assert.Empty(services); }
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ResolutionIsInertAndFastFilterCannotOpenVolumeHttpOrKeys()
    {
        var root = MissingRoot(); var configuration = Configuration(Settings(root)); var keys = new NoSecrets();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(new CompositeSecretResolver([keys]));
        Assert.True(services.AddGroupConnectorRecovery(configuration, "Production", true));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var runtime = provider.GetRequiredService<GroupConnectorRecoveryRuntime>();
        Assert.IsType<GroupConnectorRecoveryHostedService>(Assert.Single(provider.GetServices<IHostedService>()));
        Assert.False(Directory.Exists(root)); Assert.Equal(0, keys.Calls);
        var identity = new GroupExternalIdentity("owned-provider", "owned-account", "owned-group");
        var payload = new GroupIngressPayload(new(identity, "owned-message", "owned-event", "owned-sender", null, GroupSourceEventKind.NewText,
            GroupServiceAuthenticatorTests.Fixture.Now, new string('A', 64), false), "PRIVATE_INVALID_TEXT", true, false, false, Guid.NewGuid(), 1);
        foreach (var rejected in new[] { payload with { IsGroup = false }, payload with { IsSelf = true }, payload with { IsKnownReportEcho = true },
            payload with { Event = payload.Event with { Identity = identity with { GroupId = "unenrolled" } } } })
            await Assert.ThrowsAsync<GroupConnectorTransportException>(() => runtime.CaptureAsync(rejected));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CaptureAsync(payload, canceled.Token));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => runtime.RecoverOnceAsync()); // Missing existing private root refuses before HTTP/keys.
        Assert.False(Directory.Exists(root)); Assert.Equal(0, keys.Calls);
        runtime.Stop(); await Assert.ThrowsAsync<GroupConnectorTransportException>(() => runtime.RecoverOnceAsync());
        runtime.Dispose(); runtime.Dispose(); Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void HostBindingCannotBeReplacedOrFollowMutableConfiguration()
    {
        var settings = Settings(MissingRoot()); var configuration = Configuration(settings); var services = new ServiceCollection();
        Assert.True(services.AddGroupConnectorRecovery(configuration, "Production", true)); var original = services.ToArray();
        Assert.True(services.AddGroupConnectorRecovery(configuration, "Production", true)); Assert.Equal(original, services.ToArray());
        configuration[Prefix + "Sources:0:ExternalGroupId"] = "PRIVATE_REPLACEMENT";
        Assert.Throws<InvalidOperationException>(() => services.AddGroupConnectorRecovery(configuration, "Production", true)); Assert.Equal(original, services.ToArray());
        var binding = (GroupConnectorRecoveryHostBinding)services.Single(item => item.ServiceType == typeof(GroupConnectorRecoveryHostBinding)).ImplementationInstance!;
        var sources = (IReadOnlyList<GroupConnectorEnrollmentRequest>)typeof(GroupConnectorRecoveryHostBinding).GetProperty("Sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding)!;
        Assert.Equal("owned-group", Assert.Single(sources).Identity.GroupId);
        Assert.Throws<NotSupportedException>(() => ((IList<GroupConnectorEnrollmentRequest>)sources)[0] = new(new(Tenant, Company, Source), new("owned-provider", "owned-account", "PRIVATE_MUTATION")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupRolesCannotReplaceProcessAuthorityInEitherRegistrationOrder(bool recoveryFirst)
    {
        var settings = Settings(MissingRoot()); settings["AIOffice:GroupIntake:PipelineEnabled"] = "true";
        foreach (var pair in new Dictionary<string, string>
        {
            ["TenantId"] = Tenant.ToString("D"),
            ["CompanyId"] = Guid.NewGuid().ToString("D"),
            ["ServiceId"] = Service.ToString("D"),
            ["CredentialEpoch"] = "7"
        }) settings["AIOffice:GroupIntake:Worker:" + pair.Key] = pair.Value;
        var config = Configuration(settings); var services = new ServiceCollection();
        if (recoveryFirst)
        {
            Assert.True(services.AddGroupConnectorRecovery(config, "Production", true)); var original = services.ToArray();
            Assert.Throws<InvalidOperationException>(() => services.AddGroupIngressReferenceConsumer(config, true)); Assert.Equal(original, services.ToArray());
        }
        else
        {
            Assert.True(services.AddGroupIngressReferenceConsumer(config, true)); var original = services.ToArray();
            Assert.Throws<InvalidOperationException>(() => services.AddGroupConnectorRecovery(config, "Production", true)); Assert.Equal(original, services.ToArray());
        }
    }

    [Fact]
    public async Task HostedRefusalUsesFixedLogAndShutdownCancelsBackoff()
    {
        var root = MissingRoot(); var services = new ServiceCollection(); var keys = new NoSecrets(); var log = new FixedLogger();
        services.AddSingleton(new CompositeSecretResolver([keys])); services.AddSingleton<ILogger<GroupConnectorRecoveryHostedService>>(log);
        Assert.True(services.AddGroupConnectorRecovery(Configuration(Settings(root)), "Production", true));
        using var provider = services.BuildServiceProvider(); var hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None); await log.Observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await hosted.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Group connector recovery is unavailable; encrypted backlog and coverage uncertainty remain.", Assert.Single(log.Messages));
        Assert.Equal(0, keys.Calls); Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task RuntimeStopCancelsUnknownCaptureAndLateAckCannotDeleteRetainedBytes()
    {
        using var fixture = new GroupConnectorSpoolTransportTests.Fixture(); var spool = fixture.Open();
        var started = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events, intercept: (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)) return null;
            started.TrySetResult(request); return release.Task;
        });
        using var runtime = InjectedSessionRuntime(fixture, spool, client);
        var capture = runtime.CaptureAsync(fixture.Auth.Payload()); var request = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var retained = Assert.Single(spool.Pending()); var bytes = fixture.Bytes(); runtime.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TimeSpan.FromSeconds(2)));
        release.SetResult(fixture.Reply(request, fixture.Receipt));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => runtime.RecoverOnceAsync());
        Assert.Equal(bytes, fixture.Bytes()); Assert.Equal(retained.Context, Assert.Single(spool.Pending()).Context); Assert.Empty(events);
        runtime.Dispose(); using var reopened = fixture.Open(); fixture.Clock.Current = GroupConnectorSpoolTransportTests.Fixture.Now.AddSeconds(31);
        using var restoredClient = fixture.Backend(new(), new());
        Assert.Equal(new GroupConnectorRecoveryPass(1, 0, 0), await fixture.Session(reopened, restoredClient).RecoverOnceAsync());
        Assert.Empty(reopened.Pending());
    }

    [Fact]
    public async Task HostedLoopReplaysThroughSharedSessionAndThenStopsWithoutProviderEffects()
    {
        using var fixture = new GroupConnectorSpoolTransportTests.Fixture(); var spool = fixture.Open(); fixture.Append(spool);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events, intercept: (request, _) =>
        { if (request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)) received.TrySetResult(); return null; });
        using var runtime = InjectedSessionRuntime(fixture, spool, client);
        using var hosted = new GroupConnectorRecoveryHostedService(runtime, TimeProvider.System, NullLogger<GroupConnectorRecoveryHostedService>.Instance);
        await hosted.StartAsync(CancellationToken.None); await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // The callback signal precedes exact ACK. Observe actual file settlement,
        // rather than treating the HTTP handler's arrival as completion.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (spool.Pending().Count != 0) await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        await hosted.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(events); Assert.Equal(new[] { GroupListenerOperation.Acquire, GroupListenerOperation.Renew }, commands.Select(command => command.Operation));
        Assert.Single(commands.Select(command => command.OwnerId).Distinct()); Assert.Empty(spool.Pending());
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => runtime.CaptureAsync(fixture.Auth.Payload()));
    }

    private static GroupConnectorRecoveryRuntime InjectedSessionRuntime(GroupConnectorSpoolTransportTests.Fixture fixture,
        GroupConnectorFileSpool spool, GroupConnectorTransportClient client)
    {
        var settings = Settings(MissingRoot());
        foreach (var pair in new Dictionary<string, string>
        {
            ["TenantId"] = fixture.Auth.Scope.TenantId.ToString("D"),
            ["CompanyId"] = fixture.Auth.Scope.CompanyId.ToString("D"),
            ["ServiceId"] = fixture.Auth.Service.Id.ToString("D"),
            ["ConnectorAccountId"] = fixture.Auth.Account.Id.ToString("D"),
            ["CredentialEpoch"] = "1",
            ["SigningSecretRef"] = "secretref://env/OWNED_GROUP_KEY",
            ["SpoolSecretRef"] = fixture.KeyBinding.Reference.Value,
            ["Provider"] = fixture.Auth.External.Provider,
            ["ExternalAccountId"] = fixture.Auth.External.AccountId,
            ["Sources:0:SourceBindingId"] = fixture.Auth.Scope.SourceBindingId.ToString("D"),
            ["Sources:0:ExternalGroupId"] = fixture.Auth.External.GroupId
        })
            settings[Prefix + pair.Key] = pair.Value;
        settings["AIOffice:GroupIntake:OwnedSyntheticFixture"] = "true"; settings["AIOffice:GroupIntake:OwnedDisposableFixture"] = "true";
        var services = new ServiceCollection(); Assert.True(services.AddGroupConnectorRecovery(Configuration(settings), "Development", true));
        var binding = (GroupConnectorRecoveryHostBinding)services.Single(item => item.ServiceType == typeof(GroupConnectorRecoveryHostBinding)).ImplementationInstance!;
        var runtime = new GroupConnectorRecoveryRuntime(binding, new([fixture.Keys]), fixture.Clock);
        // Isolated lifecycle seam only: actual file/session/lease-policy and
        // bounded fake HTTP. Native production factory/HTTP proof remains separate.
        foreach (var pair in new Dictionary<string, object> { ["session"] = fixture.Session(spool, client), ["spool"] = spool, ["client"] = client })
            typeof(GroupConnectorRecoveryRuntime).GetField(pair.Key, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runtime, pair.Value);
        return runtime;
    }

    private sealed class NoSecrets : ISecretResolver
    {
        internal int Calls;
        public string Provider => "env";
        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("PRIVATE_KEY_MUST_NOT_BE_RESOLVED"); }
    }
    private sealed class FixedLogger : ILogger<GroupConnectorRecoveryHostedService>
    {
        internal readonly List<string> Messages = [];
        internal readonly TaskCompletionSource Observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); Observed.TrySetResult(); }
    }
}
