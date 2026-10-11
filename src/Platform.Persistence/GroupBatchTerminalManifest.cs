using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Closed immutable metadata encoding, not terminal authorization. The future
// owned SQL writer must reconstruct current coverage/effects and compare these
// exact bytes inside its final authority/expiry/savepoint fences.
internal sealed class GroupBatchTerminalManifest
{
    internal const int Version = 1;
    internal const int HeaderBytes = 177;
    internal const int ContributorBytes = 80;
    internal const int MinimumBytes = HeaderBytes + ContributorBytes;
    internal const int MaximumBytes = HeaderBytes + FrozenGroupBatch.MaximumMessages * ContributorBytes;
    private static readonly byte[] Magic = "AIOGTRM1"u8.ToArray();
    private readonly byte[] encoded;
    private GroupBatchTerminalManifest(byte[] bytes, GroupScope scope, Guid batch, Guid allocationOperation, Guid operation,
        long after, long through, int raw, int selected, int notes, Contributor[] contributors)
    {
        encoded = bytes; Scope = scope; BatchId = batch; AllocationOperationId = allocationOperation; OperationId = operation;
        AfterSequence = after; ThroughSequence = through; RawCount = raw; SelectedCount = selected; NoteCount = notes;
        Contributors = Array.AsReadOnly(contributors); Fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
    }
    internal GroupScope Scope { get; }
    internal Guid BatchId { get; }
    internal Guid AllocationOperationId { get; }
    internal Guid OperationId { get; }
    internal long AfterSequence { get; }
    internal long ThroughSequence { get; }
    internal int RawCount { get; }
    internal int SelectedCount { get; }
    internal int NoteCount { get; }
    internal string Fingerprint { get; }
    internal IReadOnlyList<Contributor> Contributors { get; }
    internal sealed record Contributor(Guid OperationId, string DependencySha256, string EffectSha256);
    public override string ToString() => "Group terminal manifest (metadata prerequisite).";
    internal byte[] Write() => encoded.ToArray();

    internal static GroupBatchTerminalManifest Create(GroupWholeBatchDependencyVerdict.Current current, Guid operation)
    {
        try { return CreateCore(current, operation); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OverflowException)
        { throw Unavailable(); }
    }

    private static GroupBatchTerminalManifest CreateCore(GroupWholeBatchDependencyVerdict.Current current, Guid operation)
    {
        if (current?.Coverage is not { } coverage || operation == Guid.Empty) throw Unavailable();
        var effects = coverage.RequireOriginalEffects(current.OriginalEffects);
        using var stream = new MemoryStream(MaximumBytes);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic);
        foreach (var id in new[] { coverage.Scope.TenantId, coverage.Scope.CompanyId, coverage.Scope.SourceBindingId,
            coverage.BatchId, coverage.AllocationOperationId, operation }) writer.Write(id.ToByteArray());
        writer.Write(coverage.AfterSequence); writer.Write(coverage.ThroughSequence); writer.Write(coverage.ObservedCommittedThrough);
        writer.Write(coverage.AllocatedAtUtc.UtcTicks); writer.Write((byte)(coverage.IsHistoricalBackfill ? 1 : 0));
        writer.Write((ushort)coverage.RawCount); writer.Write((byte)coverage.Manifests.Sum(x => x.Sources.Count));
        writer.Write(coverage.NoteCount); writer.Write(Convert.FromHexString(coverage.Fingerprint)); writer.Write((byte)effects.Count);
        foreach (var effect in effects)
        {
            var manifest = coverage.Manifests.Single(x => x.OperationId == effect.OperationId);
            writer.Write(effect.OperationId.ToByteArray());
            writer.Write(Convert.FromHexString(GroupWorkDependencyManifest.Fingerprint("aioffice-group-terminal-dependency-v1", new
            {
                manifest.Scope,
                manifest.BatchId,
                manifest.OperationId,
                manifest.AllocatedThroughSequence,
                manifest.AuthoritySha256,
                manifest.CoverageSha256,
                manifest.Sources,
                manifest.Dependencies
            })));
            // The sealed original-effect fingerprint binds the complete original
            // acquisition provenance, original receipt and original effect graph.
            writer.Write(Convert.FromHexString(effect.Fingerprint));
        }
        writer.Flush(); return Read(stream.ToArray());
    }

    internal static GroupBatchTerminalManifest Read(byte[]? value)
    {
        if (value is null || value.Length is < MinimumBytes or > MaximumBytes) throw Unavailable();
        // Detach before parsing or retaining any caller-owned bytes.
        var bytes = value.ToArray();
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw Unavailable();
        var scope = new GroupScope(Id(), Id(), Id()); var batch = Id(); var allocationOperation = Id(); var operation = Id();
        var after = reader.ReadInt64(); var through = reader.ReadInt64(); var committed = reader.ReadInt64();
        var allocatedTicks = reader.ReadInt64(); var historical = reader.ReadByte(); var raw = reader.ReadUInt16();
        var selected = reader.ReadByte(); var notes = reader.ReadInt32(); _ = Hash(); var count = reader.ReadByte();
        if (after < 0 || through <= after || committed < through || through - after != raw
            || allocatedTicks < DateTimeOffset.MinValue.UtcTicks || allocatedTicks > DateTimeOffset.MaxValue.UtcTicks
            || historical > 1 || raw is < 1 or > GroupBatchAllocationPrefix.MaximumRawRevisions
            || selected is < 1 or > FrozenGroupBatch.MaximumMessages || selected > raw
            || count is < 1 or > FrozenGroupBatch.MaximumMessages || count > selected
            || notes < 0 || notes > count * GroupAutomaticNotePlan.MaximumNotes
            || bytes.Length != HeaderBytes + count * ContributorBytes) throw Unavailable();
        var contributors = new Contributor[count];
        for (var index = 0; index < contributors.Length; index++)
        {
            var id = Id();
            if (index > 0 && contributors[index - 1].OperationId.CompareTo(id) >= 0) throw Unavailable();
            contributors[index] = new(id, Hash(), Hash());
        }
        if (stream.Position != bytes.Length) throw Unavailable();
        return new(bytes, scope, batch, allocationOperation, operation, after, through, raw, selected, notes, contributors);

        Guid Id() { var id = new Guid(reader.ReadBytes(16)); return id != Guid.Empty ? id : throw Unavailable(); }
        string Hash()
        {
            var digest = reader.ReadBytes(32);
            if (digest.Length != 32 || digest.All(x => x == 0)) throw Unavailable();
            return Convert.ToHexString(digest);
        }
    }

    internal static void RequireUnchanged(GroupWholeBatchDependencyVerdict.Current current, Guid operation, byte[]? expected)
    {
        var original = Read(expected); var observed = Create(current, operation);
        if (!CryptographicOperations.FixedTimeEquals(original.encoded, observed.encoded)) throw Unavailable();
    }
    private static InvalidOperationException Unavailable() => new("Group terminal manifest is not available.");
}
