using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Versioned metadata only: no plaintext message, note, glossary, key or token.
// Captures every contributing dependency of one committed automatic chunk;
// this is not permission to advance a terminal frontier.
internal sealed class GroupWorkDependencyManifest
{
    internal const int Version = 1;
    internal const int HeaderBytes = 162;
    internal const int MinimumBytes = HeaderBytes + 56;
    internal const int MaximumBytes = HeaderBytes + 100 * 56 + 20 * 57;
    private static readonly byte[] Magic = "AIOGDEP1"u8.ToArray();

    private GroupWorkDependencyManifest(GroupScope scope, Guid batch, Guid operation, long cutoff,
        string authority, string coverage, Source[] sources, Brain[] brain)
    {
        Scope = scope; BatchId = batch; OperationId = operation; AllocatedThroughSequence = cutoff;
        AuthoritySha256 = authority; CoverageSha256 = coverage;
        Sources = Array.AsReadOnly(sources); Dependencies = Array.AsReadOnly(brain);
    }
    internal GroupScope Scope { get; }
    internal Guid BatchId { get; }
    internal Guid OperationId { get; }
    internal long AllocatedThroughSequence { get; }
    internal string AuthoritySha256 { get; }
    internal string CoverageSha256 { get; }
    internal IReadOnlyList<Source> Sources { get; }
    internal IReadOnlyList<Brain> Dependencies { get; }
    public override string ToString() => "Group work dependencies (private metadata).";
    internal sealed record Source(Guid MessageId, long Revision, string SnapshotSha256);
    internal sealed record Brain(GroupBrainContentKind Kind, Guid RecordId, long Revision, string SnapshotSha256);

    internal static byte[] Create(GroupBatchSourceContext context, GroupBrainPrivateContext brain, Guid operation)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(brain);
        if (operation == Guid.Empty || brain.Handle.Receipt != context.Handle.Receipt
            || context.Snapshots.Count is < 1 or > 100 || context.Snapshots.Count != context.Items.Count
            || brain.Snapshots.Count > GroupBrainCurrentReader.MaximumSelectedRevisions || brain.Snapshots.Count != brain.Items.Count
            || context.Snapshots.Any(x => x.Head.TenantId != context.Scope.TenantId || x.Head.CompanyId != context.Scope.CompanyId
                || x.Head.BindingId != context.Scope.SourceBindingId || !context.Items.Any(item => item.MessageId == x.Head.MessageId && item.Revision == x.Head.Revision))
            || brain.Snapshots.Any(x => x.Revision.TenantId != context.Scope.TenantId || x.Revision.CompanyId != context.Scope.CompanyId
                || x.Revision.BindingId != context.Scope.SourceBindingId || x.RecordId != x.Revision.RecordId
                || !brain.Items.Any(item => item.Kind == x.Kind && item.RecordId == x.RecordId && item.Revision == x.Revision.Revision))) throw Unavailable();
        var scope = context.Scope;
        var sources = context.Snapshots.OrderBy(x => x.Head.MessageId).Select(x => new Source(
            x.Head.MessageId, x.Head.Revision, SourceFingerprint(x))).ToArray();
        var dependencies = brain.Snapshots.OrderBy(x => x.Kind).ThenBy(x => x.RecordId).Select(x => new Brain(
            x.Kind, x.RecordId, x.Revision.Revision, BrainFingerprint(x))).ToArray();
        var manifest = new GroupWorkDependencyManifest(scope, context.BatchId, operation, context.AllocatedThroughSequence,
            GroupBatchClaimStore.AuthorityFingerprint(context.Handle.Authority), context.Coverage.Fingerprint(), sources, dependencies);
        using var stream = new MemoryStream(MaximumBytes);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic);
        foreach (var id in new[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId, context.BatchId, operation }) writer.Write(id.ToByteArray());
        writer.Write(manifest.AllocatedThroughSequence);
        writer.Write(Convert.FromHexString(manifest.AuthoritySha256)); writer.Write(Convert.FromHexString(manifest.CoverageSha256));
        writer.Write((byte)sources.Length); writer.Write((byte)dependencies.Length);
        foreach (var source in sources)
        {
            writer.Write(source.MessageId.ToByteArray()); writer.Write(source.Revision); writer.Write(Convert.FromHexString(source.SnapshotSha256));
        }
        foreach (var dependency in dependencies)
        {
            writer.Write((byte)dependency.Kind); writer.Write(dependency.RecordId.ToByteArray());
            writer.Write(dependency.Revision); writer.Write(Convert.FromHexString(dependency.SnapshotSha256));
        }
        writer.Flush();
        var bytes = stream.ToArray();
        _ = Read(bytes); // Closed size/order/identity invariants also gate creation.
        return bytes;
    }

    internal static GroupWorkDependencyManifest Read(byte[]? value)
    {
        if (value is null || value.Length is < MinimumBytes or > MaximumBytes) throw Unavailable();
        using var stream = new MemoryStream(value, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw Unavailable();
        var scope = new GroupScope(Id(), Id(), Id()); var batch = Id(); var operation = Id();
        var cutoff = reader.ReadInt64(); var authority = Hash(); var coverage = Hash();
        var sourceCount = reader.ReadByte(); var brainCount = reader.ReadByte();
        if (cutoff <= 0 || sourceCount is < 1 or > 100 || brainCount > GroupBrainCurrentReader.MaximumSelectedRevisions
            || value.Length != HeaderBytes + sourceCount * 56 + brainCount * 57) throw Unavailable();
        var sources = new Source[sourceCount];
        for (var index = 0; index < sources.Length; index++)
        {
            var id = Id(); var revision = Revision(); var hash = Hash();
            if (index > 0 && sources[index - 1].MessageId.CompareTo(id) >= 0) throw Unavailable();
            sources[index] = new(id, revision, hash);
        }
        var brain = new Brain[brainCount];
        for (var index = 0; index < brain.Length; index++)
        {
            var kind = (GroupBrainContentKind)reader.ReadByte(); var id = Id(); var revision = Revision(); var hash = Hash();
            if (kind is not (GroupBrainContentKind.RequestRevision or GroupBrainContentKind.GlossaryRevision)
                || index > 0 && (brain[index - 1].Kind > kind
                    || brain[index - 1].Kind == kind && brain[index - 1].RecordId.CompareTo(id) >= 0)) throw Unavailable();
            brain[index] = new(kind, id, revision, hash);
        }
        if (stream.Position != value.Length) throw Unavailable();
        return new(scope, batch, operation, cutoff, authority, coverage, sources, brain);

        Guid Id()
        {
            var id = new Guid(reader.ReadBytes(16));
            return id != Guid.Empty ? id : throw Unavailable();
        }
        long Revision() => reader.ReadInt64() is var revision && revision > 0 ? revision : throw Unavailable();
        string Hash() => Convert.ToHexString(reader.ReadBytes(32));
    }

    internal static void RequireReplay(GroupWorkCommitReceiptRecord receipt, byte[]? expected)
    {
        // Expand compatibility: old version0 receipts remain replayable, but
        // absence is never a terminal dependency proof. New version1 requires
        // the entire closed original bytes, not merely a count or selected set.
        if (receipt.DependencyManifestVersion == 0 && receipt.DependencyManifest is null) return;
        if (expected is null || receipt.DependencyManifestVersion != Version) throw Unavailable();
        _ = Read(receipt.DependencyManifest);
        if (!CryptographicOperations.FixedTimeEquals(receipt.DependencyManifest!, expected)) throw Unavailable();
    }

    internal static string SourceFingerprint(GroupBatchSourceSnapshot snapshot) =>
        Fingerprint("aioffice-group-source-dependency-v1", snapshot with { ProtectedContent = null }, snapshot.ProtectedContent);
    internal static string BrainFingerprint(GroupBrainSnapshot snapshot) =>
        Fingerprint("aioffice-group-brain-dependency-v1", snapshot with { Revision = snapshot.Revision with { ProtectedContent = null } }, snapshot.Revision.ProtectedContent);

    internal static string Fingerprint<T>(string domain, T metadata, byte[]? ciphertext = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        try
        {
            using var stream = new MemoryStream(bytes.Length + 256);
            try
            {
                using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
                writer.Write(domain); writer.Write(bytes.Length); writer.Write(bytes);
                writer.Write(ciphertext?.Length ?? -1);
                if (ciphertext is not null) writer.Write(SHA256.HashData(ciphertext));
                writer.Flush();
                return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
            }
            finally { CryptographicOperations.ZeroMemory(stream.GetBuffer()); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static InvalidOperationException Unavailable() => new("Group work dependencies are not available.");
}
