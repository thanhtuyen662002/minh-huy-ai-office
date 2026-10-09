using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupServiceSignature(Guid ServiceId, long CredentialEpoch,
    long SignedAtUnixSeconds, Guid Nonce, string SignatureHex);
public sealed record GroupIngressPayload(GroupSourceEventMetadata Event, string Text,
    bool IsGroup, bool IsSelf, bool IsKnownReportEcho, Guid ListenerOwnerId, long ListenerEpoch);

public sealed class VerifiedGroupIngress
{
    internal VerifiedGroupIngress(AuthenticatedGroupService service, GroupIngressPayload payload)
    { Service = service; Payload = payload; }
    internal AuthenticatedGroupService Service { get; }
    internal GroupIngressPayload Payload { get; }
    public GroupScope Source => Service.Source;
}

// The host chooses this policy once; no HTTP/model input can select a profile.
public sealed class GroupIngressRuntimePolicy
{
    private GroupIngressRuntimePolicy(bool synthetic) { IsSyntheticFixture = synthetic; }
    internal bool IsSyntheticFixture { get; }
    public static GroupIngressRuntimePolicy Live { get; } = new(false);
    public static GroupIngressRuntimePolicy OwnedSyntheticFixture(string hostEnvironment, bool ownedDisposable)
    {
        if (hostEnvironment != "Development" || !ownedDisposable)
            throw new InvalidOperationException("Synthetic group ingress requires an owned development fixture.");
        return new(true);
    }
}

public sealed class GroupServiceAuthenticator(PlatformDbContext database,
    CompositeSecretResolver secrets, GroupIngressRuntimePolicy policy, TimeProvider clock, ILogger<GroupServiceAuthenticator>? logger = null)
{
    public const int MaximumBodyBytes = 65536;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };

    public async Task<VerifiedGroupIngress> AuthenticateAsync(GroupServiceSignature signature,
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        if (signature is null || body.Length is < 1 or > MaximumBodyBytes) throw GroupServiceDirectory.Denied();
        // ReadOnlyMemory can alias caller-owned mutable bytes. Parse and sign
        // one private snapshot across all asynchronous secret/SQL work.
        var captured = body.ToArray();
        var phase = "parse";
        try { return await AuthenticateCapturedAsync(signature, captured, cancellationToken, value => phase = value); }
        catch (UnauthorizedAccessException)
        {
            // Fixed server phases only: never log events, keys, references,
            // identities, exception bodies or attacker-controlled input.
            logger?.LogWarning("group-auth-refusal: {Phase}", phase);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(captured); }
    }

    private async Task<VerifiedGroupIngress> AuthenticateCapturedAsync(GroupServiceSignature signature,
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken, Action<string> phase)
    {
        var payload = Parse(body);
        phase("signing-time");
        var now = clock.GetUtcNow();
        if (signature.ServiceId == Guid.Empty || signature.CredentialEpoch <= 0 || signature.Nonce == Guid.Empty ||
            !IsHash(signature.SignatureHex)) throw GroupServiceDirectory.Denied();
        DateTimeOffset signedAt;
        try { signedAt = DateTimeOffset.FromUnixTimeSeconds(signature.SignedAtUnixSeconds); }
        catch (ArgumentOutOfRangeException) { throw GroupServiceDirectory.Denied(); }
        RequireFreshSigningTime(signedAt, now);
        var signingBytes = SigningBytes(signature, body.Span);

        var directory = new GroupServiceDirectory(database);
        var permissions = new GroupIngressPermissionVerifier(database);
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        phase("permissions-initial");
        await permissions.RequireSafeRuntimeAsync(cancellationToken);
        phase("registry-initial");
        var authority = await directory.RequireIngestAsync(new(signature.ServiceId, signature.CredentialEpoch), payload.Event.Identity, cancellationToken);
        phase("qualification-initial");
        RequireQualification(authority.Account, payload.Event.Kind, now, policy);
        var verified = new AuthenticatedGroupService(authority, signedAt);
        byte[] key;
        try
        {
            phase("service-key");
            var value = await secrets.ResolveAsync(SecretReference.Parse(authority.CredentialReference), cancellationToken);
            if (value.Length != 44) throw GroupServiceDirectory.Denied();
            key = Convert.FromBase64String(value);
            if (key.Length != 32 || Convert.ToBase64String(key) != value)
            { CryptographicOperations.ZeroMemory(key); throw GroupServiceDirectory.Denied(); }
        }
        catch (Exception error) when (error is FormatException or NotSupportedException or InvalidOperationException)
        { throw GroupServiceDirectory.Denied(); }
        try
        {
            phase("hmac");
            var expected = HMACSHA256.HashData(key, signingBytes);
            if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature.SignatureHex)))
                throw GroupServiceDirectory.Denied();
            phase("registry-final");
            var current = await directory.RequireCurrentAsync(verified, cancellationToken);
            phase("permissions-final");
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            phase("expiry-final");
            var finalNow = clock.GetUtcNow();
            RequireFreshSigningTime(signedAt, finalNow);
            RequireQualification(current.Account, payload.Event.Kind, finalNow, policy);
            await transaction.CommitAsync(cancellationToken);
            return new(verified, payload);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static byte[] SigningBytes(GroupServiceSignature signature, ReadOnlySpan<byte> body) => Encoding.ASCII.GetBytes(
        FormattableString.Invariant($"aioffice-group-ingest-v1\n{signature.ServiceId:D}\n{signature.CredentialEpoch}\n{signature.SignedAtUnixSeconds}\n{signature.Nonce:D}\n{Convert.ToHexString(SHA256.HashData(body))}"));

    internal static void RequireFreshSigningTime(DateTimeOffset signedAtUtc, DateTimeOffset nowUtc)
    {
        if (signedAtUtc.Offset != TimeSpan.Zero || nowUtc.Offset != TimeSpan.Zero || (nowUtc - signedAtUtc).Duration() > MaximumClockSkew)
            throw GroupServiceDirectory.Denied();
    }

    internal static GroupIngressPayload Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            if (body.Length is < 1 or > MaximumBodyBytes) throw GroupServiceDirectory.Denied();
            _ = StrictUtf8.GetCharCount(body.Span);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            RequireUniqueProperties(document.RootElement);
            var payload = document.Deserialize<GroupIngressPayload>(JsonOptions) ?? throw GroupServiceDirectory.Denied();
            if (payload.Event is null || !payload.IsGroup || payload.IsSelf || payload.IsKnownReportEcho ||
                payload.ListenerOwnerId == Guid.Empty || payload.ListenerEpoch <= 0 || payload.Text is null ||
                payload.Text.Length > GroupSourceContentProtector.MaximumTextLength) throw GroupServiceDirectory.Denied();
            payload.Event.Validate();
            var contentHash = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(payload.Text)));
            if (payload.Event.ContentSha256 != contentHash) throw GroupServiceDirectory.Denied();
            return payload;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException)
        { throw GroupServiceDirectory.Denied(); }
    }

    private static void RequireUniqueProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw GroupServiceDirectory.Denied(); RequireUniqueProperties(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) RequireUniqueProperties(item);
    }

    internal static void RequireQualification(GroupConnectorAccountRecord account, GroupSourceEventKind kind, DateTimeOffset now, GroupIngressRuntimePolicy policy)
    {
        try
        {
            using var document = JsonDocument.Parse(account.QualificationJson, new JsonDocumentOptions { MaxDepth = 8 });
            RequireUniqueProperties(document.RootElement);
            var stored = document.Deserialize<RegistryQualification>(JsonOptions) ?? throw GroupServiceDirectory.Denied();
            var artifact = new GroupConnectorArtifact(account.Provider, account.PackageVersion, account.GitCommit);
            var qualification = new GroupConnectorQualification(account.TenantId, account.CompanyId, account.Id,
                account.ExternalAccountId, artifact, stored.Environment, stored.Observations);
            if (policy.IsSyntheticFixture)
            {
                if (account.Provider != "synthetic" || account.PackageVersion != "owned-fixture" ||
                    stored.Environment != GroupQualificationEnvironment.Synthetic) throw GroupServiceDirectory.Denied();
            }
            else if (account.Provider == "synthetic" || account.PackageVersion == "owned-fixture" ||
                !qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, account.TenantId, account.CompanyId,
                account.Id, account.ExternalAccountId, artifact, now)) throw GroupServiceDirectory.Denied();
            var extra = kind == GroupSourceEventKind.Edit ? GroupConnectorCapability.EditEvents :
                kind == GroupSourceEventKind.Recall ? GroupConnectorCapability.RecallEvents : (GroupConnectorCapability?)null;
            if (extra is not null && !qualification.Observations.Any(x => x.Capability == extra &&
                x.Support == GroupConnectorSupport.Supported && x.EvidenceId != Guid.Empty && x.ObservedAtUtc <= now &&
                now - x.ObservedAtUtc <= GroupConnectorQualification.MaximumObservationAge)) throw GroupServiceDirectory.Denied();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException)
        { throw GroupServiceDirectory.Denied(); }
    }

    private static bool IsHash(string value) => value is not null && value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private sealed record RegistryQualification(GroupQualificationEnvironment Environment, IReadOnlyList<GroupConnectorObservation> Observations);
}
