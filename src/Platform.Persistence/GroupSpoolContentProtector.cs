using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupSpoolContentContext(GroupScope Source, Guid ConnectorAccountId, Guid ServiceId,
    long CredentialEpoch, long SourceVersion, long GrantVersion, long DeletionGeneration,
    string ExternalIdentityHash, string EventIdentityHash, GroupSourceEventKind EventKind, string BodySha256, DateTimeOffset AdmittedAtUtc, string KeyId);

public sealed record GroupSpoolProtectedContent(GroupSpoolContentContext Context, byte[] Envelope);

public sealed class GroupSpoolCleartext : IDisposable
{
    private byte[]? body;
    internal GroupSpoolCleartext(byte[] value) { body = value; }
    public ReadOnlyMemory<byte> Body => body ?? throw new ObjectDisposedException(nameof(GroupSpoolCleartext));
    public void Dispose()
    { if (body is { } value) { CryptographicOperations.ZeroMemory(value); body = null; } }
}

// No files/keys are accessed here. Callers obtain a dedicated spool key only
// after the prefilter and zero recovered bodies after fresh admission/SQL ACK.
// This envelope conveys neither current authority nor successful SQL commit.
public sealed class GroupSpoolContentProtector
{
    public const int MaximumBodyBytes = GroupServiceAuthenticator.MaximumBodyBytes;
    public const int MaximumEnvelopeBytes = MaximumBodyBytes + 29;

    public GroupSpoolProtectedContent Protect(GroupConnectorSpoolAdmission admission, ReadOnlySpan<byte> key, string keyId)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var clear = JsonSerializer.SerializeToUtf8Bytes(admission.Payload, GroupServiceAuthenticator.JsonOptions);
        try
        {
            if (clear.Length is < 1 or > MaximumBodyBytes) throw Unavailable();
            var source = admission.Enrollment.Source;
            var context = new GroupSpoolContentContext(source.Scope, source.ConnectorAccountId, admission.Enrollment.Principal.ServiceId,
                admission.Enrollment.Principal.CredentialEpoch, source.Version, admission.Enrollment.Grant.Version, source.DeletionGeneration,
                source.ExternalIdentity.IndexKey(), GroupIngressIdentity.EventIndex(source.Scope, admission.Payload.Event.RevisionEventId),
                admission.Payload.Event.Kind, Convert.ToHexString(SHA256.HashData(clear)), admission.AdmittedAtUtc, keyId);
            var aad = AssociatedData(context, key);
            var envelope = new byte[29 + clear.Length]; envelope[0] = 1;
            RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(envelope.AsSpan(1, 12), clear, envelope.AsSpan(29), envelope.AsSpan(13, 16), aad);
            return new(context, envelope);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public GroupSpoolCleartext Unprotect(GroupSpoolContentContext context, ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key)
    {
        var aad = AssociatedData(context, key);
        if (envelope.Length is < 30 or > MaximumEnvelopeBytes || envelope[0] != 1) throw Unavailable();
        var clear = new byte[envelope.Length - 29];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Slice(1, 12), envelope[29..], envelope.Slice(13, 16), clear, aad);
            if (Convert.ToHexString(SHA256.HashData(clear)) != context.BodySha256) throw Unavailable();
            var payload = GroupServiceAuthenticator.Parse(clear);
            if (payload.Event.Kind != context.EventKind || payload.Event.Identity.IndexKey() != context.ExternalIdentityHash ||
                GroupIngressIdentity.EventIndex(context.Source, payload.Event.RevisionEventId) != context.EventIdentityHash) throw Unavailable();
            return new(clear);
        }
        catch (Exception error) when (error is CryptographicException or UnauthorizedAccessException or InvalidOperationException)
        { CryptographicOperations.ZeroMemory(clear); throw Unavailable(); }
    }

    public GroupConnectorSpoolAdmission Recover(GroupSpoolProtectedContent stored, ReadOnlySpan<byte> key,
        GroupConnectorEnrollment current, GroupListenerLeaseSnapshot lease, DateTimeOffset nowUtc, GroupIngressRuntimePolicy policy)
    {
        if (stored?.Context is not { } context || current?.Source is not { } source || current.Principal is null || current.Grant is null ||
            context.Source != source.Scope || context.ConnectorAccountId != source.ConnectorAccountId || context.ServiceId != current.Principal.ServiceId ||
            context.CredentialEpoch != current.Principal.CredentialEpoch || context.SourceVersion != source.Version || context.GrantVersion != current.Grant.Version ||
            context.DeletionGeneration != source.DeletionGeneration || context.ExternalIdentityHash != source.ExternalIdentity.IndexKey() ||
            context.AdmittedAtUtc > nowUtc) throw GroupConnectorSpoolAdmission.Denied();
        // Fresh trusted enrollment is checked before resolving private content.
        GroupConnectorSpoolAdmission.RequireCurrent(current, lease, nowUtc, policy, context.EventKind);
        using var clear = Unprotect(context, stored.Envelope, key);
        var payload = GroupServiceAuthenticator.Parse(clear.Body);
        // A restart changes transport ownership, never the logical event.
        payload = payload with { ListenerOwnerId = lease.OwnerId, ListenerEpoch = lease.Epoch };
        return GroupConnectorSpoolAdmission.Filter(current, payload, lease, nowUtc, policy);
    }

    private static byte[] AssociatedData(GroupSpoolContentContext context, ReadOnlySpan<byte> key)
    {
        static bool Hash(string? value) => value?.Length == 64 && value.All(x => x is >= '0' and <= '9' or >= 'A' and <= 'F');
        if (context?.Source is null || context.ConnectorAccountId == Guid.Empty || context.ServiceId == Guid.Empty ||
            context.CredentialEpoch <= 0 || context.SourceVersion <= 0 || context.GrantVersion <= 0 || context.DeletionGeneration < 0 ||
            !Enum.IsDefined(context.EventKind) || !Hash(context.ExternalIdentityHash) || !Hash(context.EventIdentityHash) || !Hash(context.BodySha256) || context.AdmittedAtUtc.Offset != TimeSpan.Zero ||
            key.Length != 32 || string.IsNullOrEmpty(context.KeyId) || context.KeyId.Length > 64 ||
            context.KeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw Unavailable();
        context.Source.Validate();
        return Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-spool-v1\0{context.Source.TenantId:D}/{context.Source.CompanyId:D}/{context.Source.SourceBindingId:D}/{context.ConnectorAccountId:D}/{context.ServiceId:D}/{context.CredentialEpoch}/{context.SourceVersion}/{context.GrantVersion}/{context.DeletionGeneration}/{context.ExternalIdentityHash}/{context.EventIdentityHash}/{(int)context.EventKind}/{context.BodySha256}/{context.AdmittedAtUtc.Ticks}/{context.KeyId}"));
    }
    private static InvalidOperationException Unavailable() => new("Connector spool content is unavailable.");
}
