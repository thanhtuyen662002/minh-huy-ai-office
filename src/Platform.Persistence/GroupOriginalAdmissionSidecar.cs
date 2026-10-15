using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Versioned historical provenance only. Decoding never manufactures current
// original-selection issuance or a committed journal/provider-release permit.
internal sealed class GroupOriginalAdmissionSidecar
{
    internal const int Version = 1;
    internal const int MaximumBytes = 1010000;
    private static readonly byte[] Magic = "AIOGOAD1"u8.ToArray();
    private readonly byte[] encoded;
    private GroupOriginalAdmissionSidecar(GroupScope scope, Guid batchId, string planSha256,
        Guid originalClaimOperationId, long originalClaimEpoch, Entry[] entries, byte[] encoded)
    {
        Scope = scope; BatchId = batchId; PlanSha256 = planSha256;
        OriginalClaimOperationId = originalClaimOperationId; OriginalClaimEpoch = originalClaimEpoch;
        Entries = Array.AsReadOnly(entries.ToArray()); this.encoded = encoded.ToArray();
        Sha256 = Convert.ToHexString(SHA256.HashData(encoded));
    }
    internal GroupScope Scope { get; }
    internal Guid BatchId { get; }
    internal string PlanSha256 { get; }
    internal Guid OriginalClaimOperationId { get; }
    internal long OriginalClaimEpoch { get; }
    internal IReadOnlyList<Entry> Entries { get; }
    internal string Sha256 { get; }
    internal byte[] Encode() => encoded.ToArray();
    public override string ToString() => "Group original admission sidecar (non-authorizing metadata).";

    internal sealed class Entry(int ordinal, Guid operationId, string originalInputIdentity, AiStructuredAdmissionSnapshot? snapshot)
    {
        internal int Ordinal { get; } = ordinal;
        internal Guid OperationId { get; } = operationId;
        // Legacy InputIdentity includes manifest/accounting/model/budget. This
        // is deliberately distinct from plain snapshot input UTF8 SHA256.
        internal string OriginalInputIdentity { get; } = originalInputIdentity;
        internal AiStructuredAdmissionSnapshot? Snapshot { get; } = snapshot;
        public override string ToString() => "Group original admission chunk metadata.";
    }

    internal static GroupOriginalAdmissionSidecar Create(GroupAutomaticInputPlan original,
        GroupBatchClaimHandle claim, IReadOnlyList<AiStructuredAdmissionSnapshot?> snapshots)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(claim);
        if (original.WireFormatVersion != 3 || snapshots is null || snapshots.Count != original.Chunks.Count
            || snapshots.Count is < 1 or > 100 || original.Dependencies.Scope != claim.Receipt.Scope
            || original.Dependencies.BatchId != claim.Receipt.BatchId) throw Unavailable();
        var entries = original.Chunks.Select((chunk, index) =>
        {
            if (chunk.Ordinal != index + 1 || chunk.RequiresModel != (snapshots[index] is not null)) throw Unavailable();
            return new Entry(chunk.Ordinal, chunk.OperationId, chunk.InputSha256, snapshots[index]);
        }).ToArray();
        var receipt = claim.Receipt;
        return Read(Write(receipt.Scope, receipt.BatchId, original.Sha256, receipt.OperationId, receipt.Epoch, entries));
    }

    internal void RequirePlan(GroupAutomaticInputPlan original)
    {
        if (original.WireFormatVersion != 3 || original.Sha256 != PlanSha256
            || original.Dependencies.Scope != Scope || original.Dependencies.BatchId != BatchId
            || original.Chunks.Count != Entries.Count) throw Unavailable();
        for (var index = 0; index < Entries.Count; index++)
        {
            var entry = Entries[index]; var chunk = original.Chunks[index];
            if (entry.Ordinal != chunk.Ordinal || entry.OperationId != chunk.OperationId || entry.OriginalInputIdentity != chunk.InputSha256
                || (entry.Snapshot is not null) != chunk.RequiresModel) throw Unavailable();
        }
    }

    internal static GroupOriginalAdmissionSidecar Read(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 211 || bytes.Length > MaximumBytes) throw Unavailable();
        var frozen = bytes.ToArray(); var bodyLength = frozen.Length - 32;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(frozen.AsSpan(0, bodyLength)), frozen.AsSpan(bodyLength))) throw Unavailable();
        try
        {
            using var stream = new MemoryStream(frozen, 0, bodyLength, false); using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (!Bytes(8).SequenceEqual(Magic)) throw Unavailable();
            var scope = new GroupScope(Id(), Id(), Id()); scope.Validate();
            var batch = Id(); var plan = Hash(); var claim = Id(); var epoch = reader.ReadInt64();
            if (epoch <= 0) throw Unavailable();
            var count = reader.ReadByte(); if (count is < 1 or > 100) throw Unavailable();
            var entries = new Entry[count];
            for (var index = 0; index < count; index++)
            {
                var ordinal = reader.ReadByte(); var operation = Id(); var input = Hash(); var tag = reader.ReadByte();
                if (ordinal != index + 1 || tag > 1) throw Unavailable();
                AiStructuredAdmissionSnapshot? snapshot = null;
                if (tag == 1)
                {
                    var length = reader.ReadInt32();
                    if (length is < 256 or > GroupStructuredAdmissionSnapshotCodec.MaximumBytes) throw Unavailable();
                    snapshot = GroupStructuredAdmissionSnapshotCodec.Read(Bytes(length));
                }
                entries[index] = new(ordinal, operation, input, snapshot);
            }
            if (stream.Position != bodyLength || entries.Select(x => x.OperationId).Distinct().Count() != entries.Length) throw Unavailable();
            if (!CryptographicOperations.FixedTimeEquals(Write(scope, batch, plan, claim, epoch, entries), frozen)) throw Unavailable();
            return new(scope, batch, plan, claim, epoch, entries, frozen);

            byte[] Bytes(int count)
            {
                if (count < 0 || count > stream.Length - stream.Position) throw Unavailable();
                var value = reader.ReadBytes(count); if (value.Length != count) throw Unavailable(); return value;
            }
            Guid Id() { var id = new Guid(Bytes(16)); if (id == Guid.Empty) throw Unavailable(); return id; }
            string Hash() => Convert.ToHexString(Bytes(32));
        }
        catch (Exception error) when (error is ArgumentException or IOException or OverflowException or InvalidOperationException) { throw Unavailable(); }
    }

    private static byte[] Write(GroupScope scope, Guid batch, string plan, Guid claim, long epoch, Entry[] entries)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic);
        foreach (var id in new[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId, batch }) writer.Write(id.ToByteArray());
        writer.Write(Convert.FromHexString(plan)); writer.Write(claim.ToByteArray()); writer.Write(epoch); writer.Write(checked((byte)entries.Length));
        foreach (var entry in entries)
        {
            writer.Write(checked((byte)entry.Ordinal)); writer.Write(entry.OperationId.ToByteArray()); writer.Write(Convert.FromHexString(entry.OriginalInputIdentity));
            writer.Write((byte)(entry.Snapshot is null ? 0 : 1));
            if (entry.Snapshot is not null)
            {
                var snapshot = GroupStructuredAdmissionSnapshotCodec.Write(entry.Snapshot);
                writer.Write(snapshot.Length); writer.Write(snapshot);
            }
            if (stream.Length + 32 > MaximumBytes) throw Unavailable();
        }
        writer.Flush(); var body = stream.ToArray(); return [.. body, .. SHA256.HashData(body)];
    }
    internal static InvalidOperationException Unavailable() => new("Group original admission provenance is not available.");
}
