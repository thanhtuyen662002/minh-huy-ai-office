using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupSourceContentContext(GroupScope Source, Guid MessageId,
    long Revision, long SourceVersion, long DeletionGeneration);

/// <summary>Protects original source; callers must obtain keys only after fresh scope authorization.</summary>
public sealed class GroupSourceContentProtector
{
    public const int MaximumTextLength = 8000;
    public const int MaximumEnvelopeLength = 65536;
    private const int PrefixLength = 1 + 12 + 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public byte[] Protect(GroupSourceContentContext context, string text, ReadOnlySpan<byte> key, string keyId)
    {
        var aad = AssociatedData(context, key, keyId);
        if (text is null || text.Length > MaximumTextLength)
            throw new InvalidOperationException("Group source content is invalid.");
        byte[] clear;
        try { clear = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw new InvalidOperationException("Group source content is invalid."); }
        try
        {
            var result = new byte[checked(PrefixLength + clear.Length)];
            result[0] = 1;
            RandomNumberGenerator.Fill(result.AsSpan(1, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(1, 12), clear, result.AsSpan(PrefixLength), result.AsSpan(13, 16), aad);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public string Unprotect(GroupSourceContentContext context, ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, string keyId)
    {
        var aad = AssociatedData(context, key, keyId);
        if (envelope.Length is < PrefixLength or > MaximumEnvelopeLength || envelope[0] != 1)
            throw new InvalidOperationException("Group source content is unavailable.");
        var clear = new byte[envelope.Length - PrefixLength];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Slice(1, 12), envelope[PrefixLength..], envelope.Slice(13, 16), clear, aad);
            var text = StrictUtf8.GetString(clear);
            if (text.Length > MaximumTextLength) throw new InvalidOperationException("Group source content is unavailable.");
            return text;
        }
        catch (CryptographicException) { throw new InvalidOperationException("Group source content is unavailable."); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("Group source content is unavailable."); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[] AssociatedData(GroupSourceContentContext context, ReadOnlySpan<byte> key, string keyId)
    {
        if (context?.Source is null || context.MessageId == Guid.Empty || context.Revision <= 0 ||
            context.SourceVersion <= 0 || context.DeletionGeneration < 0 || key.Length != 32 ||
            string.IsNullOrEmpty(keyId) || keyId.Length > 64 ||
            keyId.Any(c => c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '-' and not '_'))
            throw new InvalidOperationException("Group source protection is invalid.");
        context.Source.Validate();
        return Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-source-v1\0{context.Source.TenantId:D}/{context.Source.CompanyId:D}/{context.Source.SourceBindingId:D}/{context.MessageId:D}/{context.Revision}/{context.SourceVersion}/{context.DeletionGeneration}/{keyId}"));
    }
}
