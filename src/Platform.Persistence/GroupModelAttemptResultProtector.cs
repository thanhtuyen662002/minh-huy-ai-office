using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed record GroupModelAttemptResultProtectionContext(GroupScope Scope, Guid BatchId,
    Guid RequestId, string PlanSha256, int ChunkOrdinal, Guid OperationId, string InputSha256)
{
    public override string ToString() => "Group model attempt result protection (private metadata).";
}

// Uses the existing scoped source key provider. The SQL store must authorize
// the current scope before resolving a key and authenticate the original plan
// and journal row again before accepting plaintext or committing any effect.
// Ciphertext alone grants no source, history or model execution authority.
internal sealed class GroupModelAttemptResultProtector
{
    internal const int MinimumClearLength = 200;
    internal const int MaximumEnvelopeLength = 29 + GroupModelAttemptResult.MaximumBytes;
    private const int PrefixLength = 1 + 12 + 16;

    internal byte[] Protect(GroupModelAttemptResultProtectionContext context, ReadOnlySpan<byte> clear,
        ReadOnlySpan<byte> key, string keyId)
    {
        var aad = AssociatedData(context, key, keyId);
        if (clear.Length is < MinimumClearLength or > GroupModelAttemptResult.MaximumBytes) throw Unavailable();
        var envelope = new byte[PrefixLength + clear.Length];
        envelope[0] = 1;
        RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(envelope.AsSpan(1, 12), clear, envelope.AsSpan(PrefixLength), envelope.AsSpan(13, 16), aad);
        return envelope;
    }

    // The owning store must zero the returned private bytes after decoding.
    internal byte[] Unprotect(GroupModelAttemptResultProtectionContext context, ReadOnlySpan<byte> envelope,
        ReadOnlySpan<byte> key, string keyId)
    {
        var aad = AssociatedData(context, key, keyId);
        if (envelope.Length is < PrefixLength + MinimumClearLength or > MaximumEnvelopeLength || envelope[0] != 1)
            throw Unavailable();
        var clear = new byte[envelope.Length - PrefixLength];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Slice(1, 12), envelope[PrefixLength..], envelope.Slice(13, 16), clear, aad);
            return clear;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(clear);
            throw Unavailable();
        }
    }

    private static byte[] AssociatedData(GroupModelAttemptResultProtectionContext context,
        ReadOnlySpan<byte> key, string keyId)
    {
        if (context?.Scope is null || context.Scope.TenantId == Guid.Empty || context.Scope.CompanyId == Guid.Empty
            || context.Scope.SourceBindingId == Guid.Empty || context.BatchId == Guid.Empty || context.RequestId == Guid.Empty
            || context.OperationId == Guid.Empty || context.ChunkOrdinal is < 1 or > 100
            || !Hash(context.PlanSha256) || !Hash(context.InputSha256) || key.Length != 32
            || string.IsNullOrEmpty(keyId) || keyId.Length > 64
            || keyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw Unavailable();
        return Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-attempt-result-v1\0{context.Scope.TenantId:D}/{context.Scope.CompanyId:D}/{context.Scope.SourceBindingId:D}/{context.BatchId:D}/{context.RequestId:D}/{context.PlanSha256}/{context.ChunkOrdinal}/{context.OperationId:D}/{context.InputSha256}/{keyId}"));
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static InvalidOperationException Unavailable() => new("Group model attempt protected result is unavailable.");
}
