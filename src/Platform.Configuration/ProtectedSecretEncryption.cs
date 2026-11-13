using System.Security.Cryptography;
using System.Text;

namespace MinhHuy.AIOffice.Platform.Configuration;

public sealed record ProtectedSecretPayload(byte[] Ciphertext, byte[] Nonce, byte[] Tag);

/// <summary>AES-256-GCM with versioned, unambiguous tenant/company/binding associated data.</summary>
public sealed class ProtectedSecretEncryption(Func<string?>? keySource = null)
{
    public const string KeyVariable = "AIOFFICE_SECRET_ENCRYPTION_KEY";
    private readonly Func<string?> readKey = keySource ?? (() => Environment.GetEnvironmentVariable(KeyVariable));
    private static InvalidOperationException Unavailable() => new("Protected credentials are unavailable.");

    private byte[] Key()
    {
        try
        {
            var encoded = readKey();
            if (string.IsNullOrWhiteSpace(encoded)) throw Unavailable();
            var key = Convert.FromBase64String(encoded);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
        }
        catch (FormatException) { }
        throw Unavailable();
    }

    /// <summary>Per-source connection strings.</summary>
    public const string SourcePurpose = "protected";
    /// <summary>Shared ERP server profiles (address and login without a database); never decrypts as a source secret.</summary>
    public const string ErpServerPurpose = "erp-server";
    /// <summary>Signing keys of web ERP clients that call the chat API.</summary>
    public const string ErpChatPurpose = "erp-chat";

    private static byte[] Aad(Guid tenant, Guid company, Guid binding, string purpose)
    {
        if (tenant == Guid.Empty || company == Guid.Empty || binding == Guid.Empty
            || purpose is not (SourcePurpose or ErpServerPurpose or ErpChatPurpose)) throw Unavailable();
        return Encoding.UTF8.GetBytes($"aioffice.{purpose}.v1/{tenant:D}/{company:D}/{binding:D}");
    }

    public ProtectedSecretPayload Encrypt(Guid tenant, Guid company, Guid binding, string plaintext, string purpose = SourcePurpose)
    {
        var key = Key();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[bytes.Length];
            var tag = new byte[16];
            using var cipher = new AesGcm(key, 16);
            cipher.Encrypt(nonce, bytes, ciphertext, tag, Aad(tenant, company, binding, purpose));
            return new(ciphertext, nonce, tag);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Decrypt(Guid tenant, Guid company, Guid binding, ProtectedSecretPayload payload, string purpose = SourcePurpose)
    {
        var key = Key();
        var bytes = new byte[payload.Ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, bytes, Aad(tenant, company, binding, purpose));
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException) { throw Unavailable(); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(bytes); }
    }

    // A keyed digest prevents an audit-row leak becoming a password guessing oracle.
    public string Fingerprint(string request)
    {
        var key = Key();
        var bytes = Encoding.UTF8.GetBytes(request);
        try { return Convert.ToHexString(HMACSHA256.HashData(key, bytes)); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(bytes); }
    }
}
