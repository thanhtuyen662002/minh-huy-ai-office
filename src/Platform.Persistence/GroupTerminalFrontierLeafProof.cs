using System.Security.Cryptography;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Pure comparison of the caller's actual descending TOP(2) snapshot. Only the
// future owned SQL store proves that these remain the latest rows after key
// await, establishes trusted genesis/original history, and writes a cursor.
internal static class GroupTerminalFrontierLeafProof
{
    internal static GroupTerminalAdvanceIntegrity? Require(GroupScope scope, IEnumerable<GroupTerminalAdvanceRecord> latestRows,
        GroupTerminalFrontierStateRecord? cursor, GroupSourceKeyMaterial? latestKey,
        GroupSourceKeyMaterial? predecessorKey, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested(); scope.Validate();
            var rows = new List<GroupTerminalAdvanceRecord>(2);
            foreach (var row in latestRows)
            {
                token.ThrowIfCancellationRequested();
                if (row is null || rows.Count == 2) throw Unavailable(); rows.Add(row);
            }
            if (cursor is not null && (cursor.TenantId != scope.TenantId || cursor.CompanyId != scope.CompanyId
                || cursor.BindingId != scope.SourceBindingId || cursor.UpdatedAtUtc.Offset != TimeSpan.Zero)) throw Unavailable();
            if (rows.Count == 0)
            {
                if (latestKey is not null || predecessorKey is not null || cursor is not null
                    && (cursor.Version != 0 || cursor.ThroughSequence != 0 || cursor.LastTerminalBatchId is not null)) throw Unavailable();
                token.ThrowIfCancellationRequested(); return null;
            }
            if (cursor is null || latestKey is null) throw Unavailable();
            var latest = rows[0]; GroupTerminalAdvanceIntegrity? previous = null;
            if (latest.Version == 1)
            {
                if (rows.Count != 1 || predecessorKey is not null) throw Unavailable();
            }
            else
            {
                if (rows.Count != 2 || predecessorKey is null) throw Unavailable();
                var predecessor = rows[1]; var sourceEnvelope = predecessor.Envelope; var sourceFingerprint = predecessor.Fingerprint;
                if (sourceEnvelope is not { Length: >= GroupTerminalAdvanceIntegrity.MinimumBytes and <= GroupTerminalAdvanceIntegrity.MaximumBytes }
                    || sourceFingerprint is not { Length: 32 }) throw Unavailable();
                var envelope = sourceEnvelope.ToArray(); var fingerprint = sourceFingerprint.ToArray();
                previous = GroupTerminalAdvanceIntegrity.Require(envelope, scope, predecessorKey);
                var metadata = previous.Metadata;
                // These are the only predecessor projections relied upon here.
                // Its other SQL projections are not claimed to be reconstructed
                // history. The signed envelope supplies the inductive anchor.
                if (predecessor.TenantId != scope.TenantId || predecessor.CompanyId != scope.CompanyId || predecessor.BindingId != scope.SourceBindingId
                    || predecessor.Version != metadata.Version || predecessor.ThroughSequence != metadata.ThroughSequence
                    || predecessor.AdvancedAtUtc != metadata.AdvancedAtUtc || predecessor.AdvancedAtUtc.Offset != TimeSpan.Zero
                    || predecessor.EnvelopeVersion != 1 || predecessor.KeyId != metadata.KeyId
                    || !CryptographicOperations.FixedTimeEquals(fingerprint, Convert.FromHexString(previous.Fingerprint))) throw Unavailable();
            }
            var authenticated = GroupTerminalAdvanceRowProof.Require(latest, scope, latestKey, previous);
            var value = authenticated.Metadata;
            if (cursor.Version != value.Version || cursor.ThroughSequence != value.ThroughSequence
                || cursor.LastTerminalBatchId != value.BatchId || cursor.UpdatedAtUtc != value.AdvancedAtUtc) throw Unavailable();
            token.ThrowIfCancellationRequested(); return authenticated;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Group terminal frontier leaf is not available.");
}
