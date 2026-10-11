using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Pins only resolved material used by the caller. The future owned store must
// load a finite retained-key inventory if it requires every older key on a call.
internal static class GroupTerminalAdvanceKeyProof
{
    internal static byte[] Fingerprint(GroupScope scope, GroupSourceKeyMaterial key)
    {
        byte[]? derived = null;
        try
        {
            scope.Validate();
            var salt = Encoding.ASCII.GetBytes(FormattableString.Invariant(
                $"aioffice-group-terminal-enrollment-key-v1\0{scope.TenantId:D}/{scope.CompanyId:D}/{scope.SourceBindingId:D}/{key.KeyId}"));
            derived = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Key, derived, salt,
                "aioffice-group-terminal-advance-key-enrollment-v1"u8);
            return SHA256.HashData(derived);
        }
        catch (Exception) { throw Unavailable(); }
        finally { if (derived is not null) CryptographicOperations.ZeroMemory(derived); }
    }

    internal static GroupTerminalAdvanceKeyRecord Stage(GroupExtractionAuthority authority, GroupSourceKeyMaterial key, DateTimeOffset now)
    {
        try
        {
            var scope = authority.Source.Scope;
            GroupRoutingPolicy.AuthorizeExtraction(new(authority.Principal.ServiceId, authority.Principal.CredentialEpoch),
                authority.Principal, authority.Grant, authority.Source);
            var record = new GroupTerminalAdvanceKeyRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                KeyId = key.KeyId,
                FormatVersion = 1,
                MaterialFingerprint = Fingerprint(scope, key),
                EnrolledAtUtc = now,
                ServiceId = authority.Principal.ServiceId,
                CredentialEpoch = authority.Principal.CredentialEpoch,
                GrantVersion = authority.Grant.Version,
                SourceVersion = authority.Source.Version,
                DeletionGeneration = authority.Source.DeletionGeneration,
                AccountVersion = authority.AccountVersion,
                AuthoritySha256 = Convert.FromHexString(GroupBatchClaimStore.AuthorityFingerprint(authority))
            };
            Require(record, scope, key); return record;
        }
        catch (Exception) { throw Unavailable(); }
    }

    internal static void Require(GroupTerminalAdvanceKeyRecord row, GroupScope scope, GroupSourceKeyMaterial key)
    {
        try
        {
            scope.Validate();
            if (row is null) throw Unavailable();
            var material = row.MaterialFingerprint; var authority = row.AuthoritySha256;
            if (material is not { Length: 32 } || authority is not { Length: 32 }) throw Unavailable();
            var ownedMaterial = material.ToArray(); var ownedAuthority = authority.ToArray();
            if (row.TenantId != scope.TenantId || row.CompanyId != scope.CompanyId || row.BindingId != scope.SourceBindingId
                || row.KeyId != key.KeyId || row.FormatVersion != 1 || row.EnrolledAtUtc.Offset != TimeSpan.Zero
                || row.ServiceId == Guid.Empty || row.CredentialEpoch <= 0 || row.GrantVersion <= 0 || row.SourceVersion <= 0
                || row.DeletionGeneration < 0 || row.AccountVersion <= 0
                || ownedAuthority.AsSpan().IndexOfAnyExcept((byte)0) < 0
                || !CryptographicOperations.FixedTimeEquals(ownedMaterial, Fingerprint(scope, key))) throw Unavailable();
        }
        catch (Exception) { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Group terminal key enrollment is not available.");
}
