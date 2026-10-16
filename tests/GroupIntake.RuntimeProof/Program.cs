using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinhHuy.AIOffice.GroupIntake.RuntimeProof;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

var stage = "owned-guard";
try
{
    if (args.Length != 1 || args[0] is not ("acquire" or "capture" or "capture-send" or "replay" or "deny" or "host-capture-send" or "host-recover" or "host-deny")) throw new InvalidOperationException();
    var managed = args[0].StartsWith("host-", StringComparison.Ordinal);
    var root = managed ? OwnedGroupProofGuard.RequireOwnedRecovery(Environment.GetEnvironmentVariable) : OwnedGroupProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
    stage = "owned-config";
    for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException();
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };
    byte[] Read(string name)
    {
        var file = new FileInfo(Path.Combine(root, name));
        if (file.Length is < 1 or > 8192 || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException();
        return File.ReadAllBytes(file.FullName);
    }
    var config = JsonSerializer.Deserialize<ProofConfiguration>(Read("config.json"), jsonOptions) ?? throw new InvalidOperationException();
    var scope = new GroupScope(config.TenantId, config.CompanyId, config.SourceId); scope.Validate();
    if (new[] { config.AccountId, config.ServiceId, config.OwnerId, config.EventId }.Contains(Guid.Empty) ||
        config.OccurredAtUtc.Offset != TimeSpan.Zero || config.OccurredAtUtc > DateTimeOffset.UtcNow ||
        !Uri.TryCreate(config.Origin, UriKind.Absolute, out var origin) || origin.Host != "127.0.0.1" || origin.Scheme != "http" ||
        origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0) throw new InvalidOperationException();
    var external = new GroupExternalIdentity("synthetic", "spool-account-" + config.AccountId.ToString("D"), "spool-group-" + config.SourceId.ToString("D"));
    var account = new GroupListenerAccountScope(config.TenantId, config.CompanyId, config.AccountId);
    var enrollmentRequest = new GroupConnectorEnrollmentRequest(scope, external);
    var policy = GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true);
    var countedSecrets = new OwnedProofSecrets();
    var secrets = new CompositeSecretResolver([countedSecrets]);
    if (managed)
    {
        stage = args[0];
        await OwnedRecoveryHostProof.RunAsync(args[0], root, config, external, countedSecrets, secrets);
        return 0;
    }
    using var client = new GroupConnectorTransportClient(origin,
        new(config.TenantId, config.CompanyId, config.ServiceId, 1, SecretReference.Parse("secretref://env/AIOFFICE_GROUP_PROOF_SIGNING_KEY")), secrets, TimeProvider.System, policy);
    void Save(string name, object value) => File.WriteAllBytes(Path.Combine(root, name), JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions));
    GroupListenerLeaseSnapshot Lease() => (JsonSerializer.Deserialize<GroupListenerCommittedReceipt>(Read("lease.json"), jsonOptions)
        ?? throw new InvalidOperationException()).Lease;
    async Task<GroupConnectorEnrollment> CurrentAsync()
    {
        var current = await client.FetchEnrollmentAsync(enrollmentRequest);
        if (current.Enrollment.Source.ConnectorAccountId != config.AccountId) throw new InvalidOperationException();
        return current.Enrollment;
    }
    stage = args[0];
    if (args[0] == "acquire")
    {
        var command = new GroupListenerCommand(config.OwnerId, GroupListenerOperation.Acquire, 0);
        using var prepared = await client.PrepareListenerAsync(await CurrentAsync(), command);
        Save("lease.json", await client.SendListenerAsync(prepared));
    }
    else
    {
        var lease = Lease();
        GroupConnectorSpoolAdmission? admission = null;
        if (args[0] is "capture" or "capture-send")
        {
            var enrollment = await CurrentAsync();
            using var renewal = await client.PrepareListenerAsync(enrollment, new(lease.OwnerId, GroupListenerOperation.Renew, lease.Epoch));
            var currentLease = await client.SendListenerAsync(renewal);
            lease = currentLease.Lease; Save("lease.json", currentLease);
            const string text = "owned native spool 😀\uFEFF ";
            var payload = new GroupIngressPayload(new(external, "spool-message-" + config.EventId, "spool-event-" + config.EventId,
                "owned-sender", null, GroupSourceEventKind.NewText, config.OccurredAtUtc, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), false),
                text, true, false, false, lease.OwnerId, lease.Epoch);
            admission = GroupConnectorSpoolAdmission.Filter(enrollment, payload, lease, DateTimeOffset.UtcNow, policy);
        }
        // Filter a new event before touching its spool or resolving any key.
        using var spool = GroupConnectorFileSpool.Open(root, account, config.ServiceId);
        if (admission is not null)
        {
            var encoded = await secrets.ResolveAsync(SecretReference.Parse("secretref://env/AIOFFICE_GROUP_PROOF_SPOOL_KEY"));
            var key = Convert.FromBase64String(encoded);
            try
            {
                if (key.Length != 32 || encoded.Length != 44 || Convert.ToBase64String(key) != encoded) throw new InvalidOperationException();
                for (var i = 0; i < 100; i++) spool.Append(new GroupSpoolContentProtector().Protect(admission, key, "spool-v1"));
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            if (args[0] == "capture") { Console.WriteLine("PASS owned native capture100 one retained encrypted file"); return 0; }
        }
        var reference = spool.Pending().Single();
        var itemPath = Path.Combine(root, "group-spool", $"{account.TenantId:N}_{account.CompanyId:N}_{account.ConnectorAccountId:N}_{config.ServiceId:N}", reference.Context.EventIdentityHash + ".spool");
        var before = File.ReadAllBytes(itemPath);
        var replay = new GroupConnectorSpoolTransport(spool, client,
            new(config.TenantId, config.CompanyId, config.AccountId, config.ServiceId, "spool-v1", SecretReference.Parse("secretref://env/AIOFFICE_GROUP_PROOF_SPOOL_KEY")), secrets, TimeProvider.System);
        if (args[0] == "deny")
        {
            var keyCalls = countedSecrets.SpoolKeyCalls;
            try { await replay.ReplayWithCurrentAuthorityAsync(reference, enrollmentRequest, lease); throw new InvalidOperationException(); }
            catch (GroupConnectorTransportException denied) when (denied.StatusCode == HttpStatusCode.Forbidden)
            {
                if (!before.SequenceEqual(File.ReadAllBytes(itemPath)) || spool.Pending().Count != 1 || countedSecrets.SpoolKeyCalls != keyCalls)
                    throw new InvalidOperationException();
                Console.WriteLine("PASS owned native fresh backend403 precedes spool key and preserves exact bytes");
            }
        }
        else
        {
            var committed = await replay.ReplayWithCurrentAuthorityAsync(reference, enrollmentRequest, lease);
            if (spool.Pending().Count != 0 || File.Exists(itemPath)) throw new InvalidOperationException();
            Save("commit.json", committed);
            Console.WriteLine("PASS owned native authenticated SQL commit deletes exact retained capture");
        }
    }
    return 0;
}
catch
{
    // Never print captured data, secrets or dependency exceptions.
    Console.Error.WriteLine("FAIL owned group spool proof at " + stage);
    return 1;
}

internal sealed record ProofConfiguration(Guid TenantId, Guid CompanyId, Guid SourceId, Guid AccountId,
    Guid ServiceId, Guid OwnerId, Guid EventId, DateTimeOffset OccurredAtUtc, string Origin);

internal sealed class OwnedProofSecrets : ISecretResolver
{
    private readonly EnvironmentVariableSecretResolver resolver = new();
    internal int SpoolKeyCalls { get; private set; }
    public string Provider => EnvironmentVariableSecretResolver.ProviderName;
    public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        if (reference.Resource == "AIOFFICE_GROUP_PROOF_SPOOL_KEY") SpoolKeyCalls++;
        return resolver.ResolveAsync(reference, cancellationToken);
    }
}
