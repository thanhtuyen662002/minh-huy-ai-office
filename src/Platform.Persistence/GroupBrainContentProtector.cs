using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public enum GroupBrainContentKind { RequestRevision = 1, GlossaryRevision = 2 }

public sealed record GroupBrainContentContext(GroupScope Source, GroupBrainContentKind Kind,
    Guid RecordId, long Revision, long SourceVersion, long DeletionGeneration);

// Protects persisted private brain payloads, not original source envelopes.
// The store must authorize the current scope and resolve the existing scoped
// key provider outside SQL; possession of this context/key grants no access.
public sealed class GroupBrainContentProtector
{
    public const int MaximumClearUtf8Bytes = 64000;
    public const int MaximumEnvelopeLength = 29 + MaximumClearUtf8Bytes;
    private const int PrefixLength = 1 + 12 + 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public byte[] Protect(GroupBrainContentContext context, string content, ReadOnlySpan<byte> key, string keyId)
    {
        var aad = AssociatedData(context, key, keyId);
        if (content is null || content.Length is < 1 or > MaximumClearUtf8Bytes) throw Unavailable();
        byte[] clear;
        try
        {
            if (StrictUtf8.GetByteCount(content) > MaximumClearUtf8Bytes) throw Unavailable();
            clear = StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException) { throw Unavailable(); }
        try
        {
            var envelope = new byte[PrefixLength + clear.Length];
            envelope[0] = 1;
            RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(envelope.AsSpan(1, 12), clear, envelope.AsSpan(PrefixLength), envelope.AsSpan(13, 16), aad);
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public string Unprotect(GroupBrainContentContext context, ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, string keyId)
    {
        var aad = AssociatedData(context, key, keyId);
        if (envelope.Length is < PrefixLength + 1 or > MaximumEnvelopeLength || envelope[0] != 1) throw Unavailable();
        var clear = new byte[envelope.Length - PrefixLength];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Slice(1, 12), envelope[PrefixLength..], envelope.Slice(13, 16), clear, aad);
            return StrictUtf8.GetString(clear);
        }
        catch (CryptographicException) { throw Unavailable(); }
        catch (DecoderFallbackException) { throw Unavailable(); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[] AssociatedData(GroupBrainContentContext context, ReadOnlySpan<byte> key, string keyId)
    {
        if (context?.Source is null || !Enum.IsDefined(context.Kind) || context.RecordId == Guid.Empty
            || context.Revision <= 0 || context.SourceVersion <= 0 || context.DeletionGeneration < 0 || key.Length != 32
            || string.IsNullOrEmpty(keyId) || keyId.Length > 64
            || keyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw Unavailable();
        if (context.Source.TenantId == Guid.Empty || context.Source.CompanyId == Guid.Empty || context.Source.SourceBindingId == Guid.Empty)
            throw Unavailable();
        return Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-brain-v1\0{context.Source.TenantId:D}/{context.Source.CompanyId:D}/{context.Source.SourceBindingId:D}/{(int)context.Kind}/{context.RecordId:D}/{context.Revision}/{context.SourceVersion}/{context.DeletionGeneration}/{keyId}"));
    }

    private static InvalidOperationException Unavailable() => new("Group brain content is unavailable.");
}
