using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Platform.Configuration;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record CustomerRequestSignature(Guid ServiceId, long Timestamp, Guid Nonce, string SignatureHex);
public sealed record CustomerRequestServiceBinding(Guid ServiceId, Guid TenantId, Guid CompanyId,
    string SecretRef, IReadOnlyList<string> AllowedSourceGroupExternalIds);

public sealed class CustomerRequestServiceDirectory
{
    private readonly Dictionary<Guid, CustomerRequestServiceBinding> bindings = [];

    public CustomerRequestServiceDirectory(IEnumerable<CustomerRequestServiceBinding> services)
    {
        foreach (var service in services)
        {
            if (service.ServiceId == Guid.Empty || service.TenantId == Guid.Empty || service.CompanyId == Guid.Empty ||
                !SecretReference.TryParse(service.SecretRef, out var reference) || reference!.Provider != "env" ||
                service.AllowedSourceGroupExternalIds is null || service.AllowedSourceGroupExternalIds.Count is < 1 or > 1000)
                throw new InvalidOperationException("Invalid customer request service configuration.");
            foreach (var group in service.AllowedSourceGroupExternalIds) CustomerRequestJson.Text(group, 200, opaque: true);
            if (service.AllowedSourceGroupExternalIds.Distinct(StringComparer.Ordinal).Count() != service.AllowedSourceGroupExternalIds.Count ||
                !bindings.TryAdd(service.ServiceId, service with { AllowedSourceGroupExternalIds = service.AllowedSourceGroupExternalIds.ToArray() }))
                throw new InvalidOperationException("Invalid customer request service configuration.");
        }
    }

    internal CustomerRequestServiceBinding Require(Guid serviceId, string group) =>
        bindings.TryGetValue(serviceId, out var binding) && binding.AllowedSourceGroupExternalIds.Contains(group, StringComparer.Ordinal)
            ? binding : throw Denied();

    internal CustomerRequestServiceBinding Require(Guid serviceId) =>
        bindings.TryGetValue(serviceId, out var binding) ? binding : throw Denied();

    internal static UnauthorizedAccessException Denied() => new("Customer request service access is unavailable.");
}

public sealed class CustomerRequestAuthenticator(CustomerRequestServiceDirectory directory, CompositeSecretResolver secrets, TimeProvider clock)
{
    public const string Domain = "aioffice-customer-request-v1";
    public const string Path = "/internal/customer-requests";
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);

    public async Task<CustomerRequestServiceBinding> AuthenticateAsync(CustomerRequestSignature signature,
        ReadOnlyMemory<byte> body, string sourceGroup, CancellationToken cancellationToken = default)
        => await AuthenticateAsync(signature, body, "POST", Path, sourceGroup, cancellationToken);

    public async Task<CustomerRequestServiceBinding> AuthenticateAsync(CustomerRequestSignature signature,
        ReadOnlyMemory<byte> body, string method, string pathAndQuery, string? sourceGroup, CancellationToken cancellationToken = default)
    {
        if (signature.ServiceId == Guid.Empty || signature.Nonce == Guid.Empty || signature.SignatureHex is null ||
            signature.SignatureHex.Length != 64 || !signature.SignatureHex.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw CustomerRequestServiceDirectory.Denied();
        RequireFresh(signature.Timestamp);
        var binding = sourceGroup is null ? directory.Require(signature.ServiceId) : directory.Require(signature.ServiceId, sourceGroup);
        // Capture the signed digest before the asynchronous resolver can yield.
        var signingBytes = SigningBytes(signature, body.Span, method, pathAndQuery);
        byte[] key;
        try
        {
            var value = await secrets.ResolveAsync(SecretReference.Parse(binding.SecretRef), cancellationToken);
            key = Convert.FromBase64String(value);
            if (key.Length != 32 || Convert.ToBase64String(key) != value)
            { CryptographicOperations.ZeroMemory(key); throw CustomerRequestServiceDirectory.Denied(); }
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or NotSupportedException)
        { throw CustomerRequestServiceDirectory.Denied(); }
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, signingBytes), Convert.FromHexString(signature.SignatureHex)))
                throw CustomerRequestServiceDirectory.Denied();
            RequireFresh(signature.Timestamp);
            return binding;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] SigningBytes(CustomerRequestSignature signature, ReadOnlySpan<byte> body) => SigningBytes(signature, body, "POST", Path);

    public static byte[] SigningBytes(CustomerRequestSignature signature, ReadOnlySpan<byte> body, string method, string pathAndQuery) => Encoding.ASCII.GetBytes(
        FormattableString.Invariant($"{Domain}\n{signature.ServiceId:D}\n{method}\n{pathAndQuery}\n{signature.Timestamp}\n{signature.Nonce:D}\n{Convert.ToHexString(SHA256.HashData(body))}"));

    internal void RequireFresh(long timestamp)
    {
        DateTimeOffset signedAt;
        try { signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
        catch (ArgumentOutOfRangeException) { throw CustomerRequestServiceDirectory.Denied(); }
        if ((clock.GetUtcNow() - signedAt).Duration() > MaximumClockSkew) throw CustomerRequestServiceDirectory.Denied();
    }
}
