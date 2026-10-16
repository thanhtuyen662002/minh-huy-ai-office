using System.Security.Cryptography;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Authenticated metadata/carrier consistency only. The future owned store
// still proves original history, fresh current authorization, retained-key
// enrollment, the latest leaf/cursor and atomic SQL write/readback.
internal static class GroupTerminalAdvanceRowProof
{
    internal static GroupTerminalAdvanceRecord Stage(GroupTerminalAdvanceIntegrity signed,
        GroupTerminalAdvanceIntegrity? predecessor)
    {
        try
        {
            var value = signed.Metadata; var previous = RequirePredecessor(value, predecessor);
            return new()
            {
                TenantId = value.Scope.TenantId,
                CompanyId = value.Scope.CompanyId,
                BindingId = value.Scope.SourceBindingId,
                Version = value.Version,
                BatchId = value.BatchId,
                TerminalOperationId = value.TerminalOperationId,
                AfterSequence = value.AfterSequence,
                ThroughSequence = value.ThroughSequence,
                TerminalManifestSha256 = value.TerminalManifestSha256.ToArray(),
                TerminalCommittedAtUtc = value.TerminalCommittedAtUtc,
                PreviousVersion = previous?.Version,
                PreviousThroughSequence = previous?.ThroughSequence,
                PreviousFingerprint = previous is null ? null : value.PreviousFingerprint.ToArray(),
                PreviousAdvancedAtUtc = previous?.AdvancedAtUtc,
                EnvelopeVersion = 1,
                Envelope = signed.Write(),
                Fingerprint = Convert.FromHexString(signed.Fingerprint),
                KeyId = value.KeyId,
                ServiceId = value.ServiceId,
                CredentialEpoch = value.CredentialEpoch,
                GrantVersion = value.GrantVersion,
                SourceVersion = value.SourceVersion,
                DeletionGeneration = value.DeletionGeneration,
                AccountVersion = value.AccountVersion,
                AuthoritySha256 = value.AuthoritySha256.ToArray(),
                AdvancedAtUtc = value.AdvancedAtUtc
            };
        }
        catch (Exception) { throw Unavailable(); }
    }

    internal static GroupTerminalAdvanceIntegrity Require(GroupTerminalAdvanceRecord row, GroupScope scope,
        GroupSourceKeyMaterial retainedKey, GroupTerminalAdvanceIntegrity? predecessor)
    {
        try
        {
            if (row is null) throw Unavailable();
            var sourceEnvelope = row.Envelope; var sourceFingerprint = row.Fingerprint;
            var sourceTerminal = row.TerminalManifestSha256; var sourceAuthority = row.AuthoritySha256;
            var sourcePrevious = row.PreviousFingerprint;
            if (sourceEnvelope is not { Length: >= GroupTerminalAdvanceIntegrity.MinimumBytes and <= GroupTerminalAdvanceIntegrity.MaximumBytes }
                || sourceFingerprint is not { Length: 32 } || sourceTerminal is not { Length: 32 }
                || sourceAuthority is not { Length: 32 } || sourcePrevious is not (null or { Length: 32 })) throw Unavailable();
            // Own the bounded mutable arrays before authenticating. No await or
            // external storage/key lookup occurs across this synchronous proof.
            var envelope = sourceEnvelope.ToArray(); var fingerprint = sourceFingerprint.ToArray();
            var terminal = sourceTerminal.ToArray(); var authority = sourceAuthority.ToArray();
            var previousFingerprint = sourcePrevious?.ToArray();
            var authenticated = GroupTerminalAdvanceIntegrity.Require(envelope, scope, retainedKey);
            var value = authenticated.Metadata; var previous = RequirePredecessor(value, predecessor);
            if (row.TenantId != value.Scope.TenantId || row.CompanyId != value.Scope.CompanyId || row.BindingId != value.Scope.SourceBindingId
                || row.Version != value.Version || row.BatchId != value.BatchId || row.TerminalOperationId != value.TerminalOperationId
                || row.AfterSequence != value.AfterSequence || row.ThroughSequence != value.ThroughSequence
                || row.TerminalCommittedAtUtc != value.TerminalCommittedAtUtc || row.TerminalCommittedAtUtc.Offset != TimeSpan.Zero
                || row.PreviousVersion != previous?.Version || row.PreviousThroughSequence != previous?.ThroughSequence
                || row.PreviousAdvancedAtUtc != previous?.AdvancedAtUtc
                || (row.PreviousAdvancedAtUtc.HasValue && row.PreviousAdvancedAtUtc.Value.Offset != TimeSpan.Zero)
                || row.EnvelopeVersion != 1 || row.KeyId != value.KeyId || row.ServiceId != value.ServiceId
                || row.CredentialEpoch != value.CredentialEpoch || row.GrantVersion != value.GrantVersion
                || row.SourceVersion != value.SourceVersion || row.DeletionGeneration != value.DeletionGeneration || row.AccountVersion != value.AccountVersion
                || row.AdvancedAtUtc != value.AdvancedAtUtc || row.AdvancedAtUtc.Offset != TimeSpan.Zero
                || !CryptographicOperations.FixedTimeEquals(fingerprint, Convert.FromHexString(authenticated.Fingerprint))
                || !CryptographicOperations.FixedTimeEquals(terminal, value.TerminalManifestSha256)
                || !CryptographicOperations.FixedTimeEquals(authority, value.AuthoritySha256)
                || (previous is null ? previousFingerprint is not null : previousFingerprint is null
                    || !CryptographicOperations.FixedTimeEquals(previousFingerprint, value.PreviousFingerprint))) throw Unavailable();
            return authenticated;
        }
        catch (Exception) { throw Unavailable(); }
    }

    private static GroupTerminalAdvanceMetadata? RequirePredecessor(GroupTerminalAdvanceMetadata value,
        GroupTerminalAdvanceIntegrity? predecessor)
    {
        if (value.Version == 1)
        {
            if (predecessor is not null) throw Unavailable(); return null;
        }
        if (predecessor is null) throw Unavailable();
        var previous = predecessor.Metadata;
        if (previous.Scope != value.Scope || previous.Version != value.PreviousVersion || previous.ThroughSequence != value.AfterSequence
            || previous.AdvancedAtUtc > value.AdvancedAtUtc
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(predecessor.Fingerprint), value.PreviousFingerprint)) throw Unavailable();
        return previous;
    }
    private static InvalidOperationException Unavailable() => new("Group terminal advance row proof is not available.");
}
