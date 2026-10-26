using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Platform.Configuration;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Same AES-256-GCM envelope convention as GroupSourceContentProtector; a separate purpose and tenant/company AAD prevent substitution.
public sealed class BotSecretProtector(CompositeSecretResolver secrets, string keySecretRef)
{
    public async Task<byte[]> ProtectAsync(Guid tenantId, Guid companyId, string value, CancellationToken ct = default)
    {
        CustomerRequestJson.Text(value, 4096, opaque: true);
        if (Encoding.UTF8.GetByteCount(value) > 4096) throw new ArgumentException("Invalid bot API key.");
        var key = await KeyAsync(ct);
        var clear = Encoding.UTF8.GetBytes(value);
        try
        {
            return AesGcmEnvelope.Protect(clear, key, Aad(tenantId, companyId));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    public async Task<string> UnprotectAsync(Guid tenantId, Guid companyId, byte[] envelope, CancellationToken ct = default)
    {
        if (envelope.Length is < 30 or > 8192 || envelope[0] != 1) throw new InvalidOperationException("Bot secret is unavailable.");
        var key = await KeyAsync(ct); var clear = new byte[envelope.Length - 29];
        try
        {
            AesGcmEnvelope.Unprotect(envelope, clear, key, Aad(tenantId, companyId));
            return new UTF8Encoding(false, true).GetString(clear);
        }
        catch (Exception error) when (error is CryptographicException or DecoderFallbackException) { throw new InvalidOperationException("Bot secret is unavailable."); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    private async Task<byte[]> KeyAsync(CancellationToken ct)
    {
        try
        {
            var value = await secrets.ResolveAsync(SecretReference.Parse(keySecretRef), ct);
            var key = Convert.FromBase64String(value);
            if (key.Length == 32 && Convert.ToBase64String(key) == value) return key;
            CryptographicOperations.ZeroMemory(key);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or NotSupportedException) { }
        throw new InvalidOperationException("Bot secret protection is unavailable.");
    }
    private static byte[] Aad(Guid tenantId, Guid companyId) => Encoding.ASCII.GetBytes($"aioffice-bot-ai-key-v1\0{tenantId:D}/{companyId:D}");
}
