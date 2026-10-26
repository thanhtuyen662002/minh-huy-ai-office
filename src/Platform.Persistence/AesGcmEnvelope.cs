using System.Security.Cryptography;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Shared version-one envelope used by source content and company bot credentials.
internal static class AesGcmEnvelope
{
    internal static byte[] Protect(ReadOnlySpan<byte> clear, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData)
    {
        var result = new byte[checked(29 + clear.Length)]; result[0] = 1;
        RandomNumberGenerator.Fill(result.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(1, 12), clear, result.AsSpan(29), result.AsSpan(13, 16), associatedData);
        return result;
    }

    internal static void Unprotect(ReadOnlySpan<byte> envelope, Span<byte> clear, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData)
    {
        if (envelope.Length < 29 || envelope[0] != 1 || clear.Length != envelope.Length - 29) throw new CryptographicException();
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(envelope.Slice(1, 12), envelope[29..], envelope.Slice(13, 16), clear, associatedData);
    }
}
