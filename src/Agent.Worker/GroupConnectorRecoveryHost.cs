using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Agent.Worker;

// Immutable trusted process configuration. It does not qualify a provider,
// enroll a source or create authority from a received event.
public sealed class GroupConnectorRecoveryHostBinding
{
    internal GroupConnectorRecoveryHostBinding(Uri origin, string root, GroupConnectorSigningBinding signing,
        GroupConnectorSpoolKeyBinding spool, GroupIngressRuntimePolicy policy, bool ownedSynthetic,
        GroupConnectorEnrollmentRequest[] sources)
    {
        Origin = origin; PrivateRoot = root; Signing = signing; Spool = spool; Policy = policy;
        OwnedSynthetic = ownedSynthetic; Sources = Array.AsReadOnly(sources.ToArray());
    }
    internal Uri Origin { get; }
    internal string PrivateRoot { get; }
    internal GroupConnectorSigningBinding Signing { get; }
    internal GroupConnectorSpoolKeyBinding Spool { get; }
    internal GroupIngressRuntimePolicy Policy { get; }
    internal bool OwnedSynthetic { get; }
    internal IReadOnlyList<GroupConnectorEnrollmentRequest> Sources { get; }
    internal bool Matches(GroupConnectorRecoveryHostBinding other) => Origin == other.Origin && PrivateRoot == other.PrivateRoot &&
        Signing == other.Signing && Spool == other.Spool && OwnedSynthetic == other.OwnedSynthetic && Sources.SequenceEqual(other.Sources);
}

public static class GroupConnectorRecoveryRegistration
{
    public static bool AddGroupConnectorRecovery(this IServiceCollection services, IConfiguration configuration, string hostEnvironment, bool hasPlatformDatabase)
    {
        if (configuration["AIOffice:GroupIntake:ConnectorRecoveryEnabled"] != "true") return false;
        if (!hasPlatformDatabase || configuration["AIOffice:GroupIntake:Enabled"] != "true") throw Unavailable();
        GroupConnectorRecoveryHostBinding binding;
        try { binding = Read(configuration, hostEnvironment); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or UnauthorizedAccessException or NotSupportedException)
        { throw Unavailable(); }
        var existing = services.Where(item => item.ServiceType == typeof(GroupConnectorRecoveryHostBinding)).ToArray();
        if (existing.Length > 0)
        {
            if (existing.Length != 1 || existing[0].ImplementationInstance is not GroupConnectorRecoveryHostBinding original || !original.Matches(binding))
                throw Unavailable();
            return true;
        }
        if (services.Any(item => item.ServiceType == typeof(GroupConnectorRecoveryRuntime) ||
            item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(GroupConnectorRecoveryHostedService))) throw Unavailable();
        foreach (var worker in services.Where(item => item.ServiceType == typeof(GroupExtractionWorkerBinding)))
            if (worker.ImplementationInstance is not GroupExtractionWorkerBinding configured || configured.TenantId != binding.Signing.TenantId ||
                configured.CompanyId != binding.Signing.CompanyId || configured.ServiceId != binding.Signing.ServiceId ||
                configured.CredentialEpoch != binding.Signing.CredentialEpoch) throw Unavailable();
        services.AddSingleton(binding);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddScoped<GroupIngressPermissionVerifier>();
        services.AddSingleton<GroupConnectorRecoveryRuntime>();
        services.AddHostedService<GroupConnectorRecoveryHostedService>();
        return true;
    }

    private static GroupConnectorRecoveryHostBinding Read(IConfiguration configuration, string environment)
    {
        var owned = configuration["AIOffice:GroupIntake:OwnedSyntheticFixture"] == "true";
        var policy = owned ? GroupIngressRuntimePolicy.OwnedSyntheticFixture(environment,
            configuration["AIOffice:GroupIntake:OwnedDisposableFixture"] == "true") : GroupIngressRuntimePolicy.Live;
        var section = configuration.GetSection("AIOffice:GroupIntake:Connector");
        static Guid Id(string? text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text ? id : throw Unavailable();
        var tenant = Id(section["TenantId"]); var company = Id(section["CompanyId"]);
        var account = Id(section["ConnectorAccountId"]); var service = Id(section["ServiceId"]);
        var epochText = section["CredentialEpoch"];
        if (!long.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch <= 0 ||
            epoch.ToString(CultureInfo.InvariantCulture) != epochText) throw Unavailable();
        if (!Uri.TryCreate(section["BackendOrigin"], UriKind.Absolute, out var origin) || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.Scheme != "https" && !(owned && origin.Scheme == "http" && origin.IsLoopback)) throw Unavailable();
        var root = section["PrivateRoot"];
        if (string.IsNullOrWhiteSpace(root) || root.Length > 1024 || !Path.IsPathFullyQualified(root)) throw Unavailable();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (root == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!)) throw Unavailable();
        var signing = SecretReference.Parse(section["SigningSecretRef"]); var encryption = SecretReference.Parse(section["SpoolSecretRef"]);
        var sameProvider = signing.Provider.Equals(encryption.Provider, StringComparison.OrdinalIgnoreCase);
        if (sameProvider && signing.Resource.Equals(encryption.Resource, signing.Provider == "env" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw Unavailable();
        var keyId = section["SpoolKeyId"];
        if (string.IsNullOrEmpty(keyId) || keyId.Length > 64 || keyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw Unavailable();
        var entries = section.GetSection("Sources").GetChildren().ToArray();
        if (entries.Length is < 1 or > 256) throw Unavailable();
        var sources = new GroupConnectorEnrollmentRequest[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Key != index.ToString(CultureInfo.InvariantCulture)) throw Unavailable();
            var scope = new GroupScope(tenant, company, Id(entries[index]["SourceBindingId"]));
            var identity = new GroupExternalIdentity(section["Provider"]!, section["ExternalAccountId"]!, entries[index]["ExternalGroupId"]!);
            scope.Validate(); identity.Validate(); sources[index] = new(scope, identity);
        }
        if (sources.Select(item => item.Source).Distinct().Count() != sources.Length ||
            sources.Select(item => item.Identity).Distinct().Count() != sources.Length) throw Unavailable();
        return new(origin, root, new(tenant, company, service, epoch, signing), new(tenant, company, account, service, keyId, encryption), policy, owned, sources);
    }

    private static InvalidOperationException Unavailable() => new("Group connector recovery configuration is not available.");
}

// Shared by the hosted recovery loop and a future qualified provider bridge.
// Resolution is inert; only an explicitly enabled operation opens the volume.
public sealed class GroupConnectorRecoveryRuntime(GroupConnectorRecoveryHostBinding binding, CompositeSecretResolver secrets, TimeProvider clock) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private GroupConnectorFileSpool? spool;
    private GroupConnectorTransportClient? client;
    private GroupConnectorRecoverySession? session;
    private bool stopped;
    private bool disposed;

    public Task<GroupIngressCommittedReceipt> CaptureAsync(GroupIngressPayload payload, CancellationToken cancellationToken = default)
    {
        if (payload is null || !payload.IsGroup || payload.IsSelf || payload.IsKnownReportEcho || payload.Event?.Identity is null ||
            !binding.Sources.Any(source => source.Identity == payload.Event.Identity)) throw new GroupConnectorTransportException();
        return RunAsync((current, token) => current.CaptureAsync(payload, token), cancellationToken);
    }
    public Task<GroupConnectorRecoveryPass> RecoverOnceAsync(CancellationToken cancellationToken = default) =>
        RunAsync((current, token) => current.RecoverOnceAsync(token), cancellationToken);

    private async Task<T> RunAsync<T>(Func<GroupConnectorRecoverySession, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        GroupConnectorRecoverySession current; CancellationTokenSource linked;
        lock (gate)
        {
            if (stopped) throw new GroupConnectorTransportException();
            token.ThrowIfCancellationRequested();
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            try
            {
                if (session is null)
                {
                    client = new(binding.Origin, binding.Signing, secrets, clock, binding.Policy);
                    spool = GroupConnectorFileSpool.Open(binding.PrivateRoot,
                        new(binding.Spool.TenantId, binding.Spool.CompanyId, binding.Spool.ConnectorAccountId), binding.Spool.ServiceId);
                    session = new(spool, client, binding.Spool, secrets, clock, binding.Policy, binding.Sources);
                }
                current = session;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            { linked.Dispose(); spool?.Dispose(); client?.Dispose(); spool = null; client = null; throw new GroupConnectorTransportException(); }
        }
        using (linked) return await operation(current, linked.Token);
    }

    public void Stop()
    {
        lock (gate) { if (stopped) return; stopped = true; shutdown.Cancel(); }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Stop(); disposed = true; spool?.Dispose(); client?.Dispose(); shutdown.Dispose();
        }
    }
}

public sealed class GroupConnectorRecoveryHostedService(GroupConnectorRecoveryRuntime runtime, TimeProvider clock,
    ILogger<GroupConnectorRecoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var refused = false;
            try { refused = (await runtime.RecoverOnceAsync(stoppingToken)).Refused > 0; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { refused = true; }
            if (refused) logger.LogWarning("Group connector recovery is unavailable; encrypted backlog and coverage uncertainty remain.");
            try { await Task.Delay(TimeSpan.FromSeconds(refused ? 5 : 1), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
    public override Task StopAsync(CancellationToken cancellationToken)
    { runtime.Stop(); return base.StopAsync(cancellationToken); }
}
