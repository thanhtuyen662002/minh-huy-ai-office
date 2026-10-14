using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Closed original selection metadata only. Neither this codec nor a supplied
// profile fingerprint qualifies a tokenizer, grants authority or invokes AI.
// A future SQL store must persist it under current locks before any attempt.
internal sealed class GroupAutomaticInputPlan
{
    internal const int MaximumBytes = 32000;
    private static readonly byte[] Magic = "AIOGPLN2"u8.ToArray();
    private static readonly byte[] MagicV3 = "AIOGPLN3"u8.ToArray();
    internal const int MaximumV2EncodedBytes = 8 + 6 * (2 + 256) + 4 + GroupWorkDependencyManifest.MaximumBytes
        + 1 + 20 * 17 + 32 + (2 + 256) + 4 + 1 + 20 * 60 + 1 + 100 * 50 + 100 * 32;
    internal const int MaximumV3EncodedBytes = MaximumV2EncodedBytes + 2 + GroupHistorySelectionDisclosure.MaximumOriginalBytes;
    private readonly byte[]? originalDisclosure;
    private readonly GroupAutomaticHistorySelection? issuedSelection;
    internal int WireFormatVersion { get; }
    internal byte[]? OriginalDisclosureBytes => originalDisclosure?.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] dependencies;
    private readonly string modelId;
    private readonly int budget;
    private readonly GroupBrainRequestOriginMetadata[] origins;

    private GroupAutomaticInputPlan(byte[] dependencies, string profile, string modelId, int budget,
        GroupBrainRequestOriginMetadata[] origins, HistoryMember[] historyOrder, Chunk[] chunks,
        int wireFormat = 2, byte[]? originalDisclosure = null, GroupAutomaticHistorySelection? issuedSelection = null)
    {
        WireFormatVersion = wireFormat; this.originalDisclosure = originalDisclosure?.ToArray(); this.issuedSelection = issuedSelection;
        this.dependencies = dependencies.ToArray(); ProfileSha256 = profile;
        this.modelId = modelId; this.budget = budget; this.origins = origins.ToArray();
        HistoryOrder = Array.AsReadOnly(historyOrder.ToArray());
        Chunks = Array.AsReadOnly(chunks.ToArray());
    }
    internal string ProfileSha256 { get; }
    internal IReadOnlyList<Chunk> Chunks { get; }
    // Full prompt order, including kind boundaries. Manifest membership is
    // sorted for dependency checks and cannot reconstruct this selection.
    internal IReadOnlyList<HistoryMember> HistoryOrder { get; }
    internal GroupWorkDependencyManifest Dependencies => GroupWorkDependencyManifest.Read(dependencies.ToArray());
    internal string Sha256 => Convert.ToHexString(SHA256.HashData(Encode()));
    public override string ToString() => "Group original input plan (private metadata).";

    internal sealed record Member(Guid MessageId, long Revision, long CommittedSequence);
    internal sealed record HistoryMember(GroupBrainContentKind Kind, Guid RecordId);
    internal sealed class Chunk
    {
        internal Chunk(int ordinal, Guid operation, bool model, string inputHash, Member[] members)
        { Ordinal = ordinal; OperationId = operation; RequiresModel = model; InputSha256 = inputHash; Members = Array.AsReadOnly(members.ToArray()); }
        internal int Ordinal { get; }
        internal Guid OperationId { get; }
        internal bool RequiresModel { get; }
        internal string InputSha256 { get; }
        internal IReadOnlyList<Member> Members { get; }
        public override string ToString() => "Group original input chunk metadata.";
    }

    internal static GroupAutomaticInputPlan Create(GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain,
        string profileSha256, string? modelId, int estimatedBudget,
        IEnumerable<GroupBrainRequestOriginMetadata> originRows, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparation); ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(originRows); token.ThrowIfCancellationRequested();
        try
        {
            if (!Hash(profileSha256)) throw Unavailable();
            var manifest = GroupWorkDependencyManifest.Create(preparation.Context, brain, preparation.Context.BatchId);
            var parsed = GroupWorkDependencyManifest.Read(manifest);
            var historyOrder = brain.Items.Select(x => new HistoryMember(x.Kind, x.RecordId)).ToArray();
            RequireHistory(parsed, historyOrder);
            var origins = SelectOrigins(parsed, originRows, token);
            var partition = GroupAutomaticContextChunks.Create(preparation, brain, modelId, estimatedBudget, token);
            var hasModel = partition.Any(x => x.ModelContext is not null);
            var chunks = partition.Select(x => new Chunk(x.Ordinal, Guid.Empty, x.ModelContext is not null,
                x.ModelContext is null
                    ? Convert.ToHexString(SHA256.HashData(GroupWorkDependencyManifest.Create(x.Preparation.Context, brain, preparation.Context.BatchId)))
                    : GroupModelAttemptBudget.InputIdentity(x.ModelContext),
                x.Preparation.Context.Items.OrderBy(y => y.CommittedSequence).ThenBy(y => y.MessageId)
                    .Select(y => new Member(y.MessageId, y.Revision, y.CommittedSequence)).ToArray())).ToArray();
            var provisional = new GroupAutomaticInputPlan(manifest, profileSha256, hasModel ? modelId! : "",
                hasModel ? estimatedBudget : 0, origins, historyOrder, chunks);
            var seed = SHA256.HashData(provisional.Write(false));
            chunks = chunks.Select(x => new Chunk(x.Ordinal, Operation(seed, x.Ordinal), x.RequiresModel, x.InputSha256, x.Members.ToArray())).ToArray();
            token.ThrowIfCancellationRequested();
            return Read(new GroupAutomaticInputPlan(manifest, profileSha256, provisional.modelId, provisional.budget, origins, historyOrder, chunks).Encode());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { throw Unavailable(); }
    }


    // Only current fenced selector issuance can construct a NEW original V3.
    internal static GroupAutomaticInputPlan CreateV3(GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain,
        GroupAutomaticHistorySelection selection, string profileSha256, string? modelId, int estimatedBudget,
        IEnumerable<GroupBrainRequestOriginMetadata> originRows, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var disclosure = GroupAutomaticHistorySelector.RequireDisclosure(selection, preparation, brain, false, token);
        return RecreateV3(preparation, brain, profileSha256, modelId, estimatedBudget, originRows,
            disclosure.EncodeOriginal(), selection, token);
    }

    // Pure preflight only: require the original fenced selector capability
    // before any SQL attempt. Current SQL origins and fences remain mandatory.
    internal void RequireOriginalCarrier(GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (WireFormatVersion != 3 || issuedSelection is null || originalDisclosure is null) throw Unavailable();
        var disclosure = GroupAutomaticHistorySelector.RequireDisclosure(issuedSelection, preparation, brain, false, token);
        if (!CryptographicOperations.FixedTimeEquals(originalDisclosure, disclosure.EncodeOriginal())) throw Unavailable();
    }

    // A checksum-valid decoded wire is structural only, not a new-row admission capability.
    internal void RequireOriginalIssuance(GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain,
        IEnumerable<GroupBrainRequestOriginMetadata> currentOrigins, CancellationToken token = default)
    {
        RequireOriginalCarrier(preparation, brain, token);
        var recreated = CreateV3(preparation, brain, issuedSelection!, ProfileSha256, modelId, budget, currentOrigins, token);
        if (!CryptographicOperations.FixedTimeEquals(Encode(), recreated.Encode())) throw Unavailable();
    }

    private static GroupAutomaticInputPlan RecreateV3(GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain,
        string profile, string? model, int budget, IEnumerable<GroupBrainRequestOriginMetadata> originRows,
        byte[] originalDisclosure, GroupAutomaticHistorySelection? selection, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(preparation); ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(originRows); token.ThrowIfCancellationRequested();
        try
        {
            if (!Hash(profile) || MaximumV3EncodedBytes > MaximumBytes) throw Unavailable();
            var manifest = GroupWorkDependencyManifest.Create(preparation.Context, brain, preparation.BatchId);
            var parsed = GroupWorkDependencyManifest.Read(manifest);
            var order = brain.Items.Select(x => new HistoryMember(x.Kind, x.RecordId)).ToArray();
            RequireHistory(parsed, order);
            var original = GroupHistorySelectionDisclosure.RestoreOriginal(originalDisclosure, preparation, brain, parsed, order, token);
            var origins = SelectOrigins(parsed, originRows, token);
            var partition = selection is null
                ? GroupAutomaticContextChunks.RestoreV2(preparation, brain, original, model, budget, token)
                : GroupAutomaticContextChunks.CreateV2(preparation, brain, selection, model, budget, token);
            var hasModel = partition.Any(x => x.ModelContext is not null);
            var chunks = partition.Select(x => new Chunk(x.Ordinal, Guid.Empty, x.ModelContext is not null,
                x.ModelContext is null ? HostV3Identity(x.Preparation, brain, originalDisclosure)
                    : GroupModelAttemptBudget.InputIdentity(x.ModelContext),
                x.Preparation.Context.Items.OrderBy(y => y.CommittedSequence).ThenBy(y => y.MessageId)
                    .Select(y => new Member(y.MessageId, y.Revision, y.CommittedSequence)).ToArray())).ToArray();
            var provisional = new GroupAutomaticInputPlan(manifest, profile, hasModel ? model! : "", hasModel ? budget : 0,
                origins, order, chunks, 3, originalDisclosure);
            var seed = SHA256.HashData(provisional.Write(false));
            chunks = chunks.Select(x => new Chunk(x.Ordinal, Operation(seed, x.Ordinal), x.RequiresModel,
                x.InputSha256, x.Members.ToArray())).ToArray();
            var frozen = Read(new GroupAutomaticInputPlan(manifest, profile, provisional.modelId, provisional.budget,
                origins, order, chunks, 3, originalDisclosure).Encode());
            token.ThrowIfCancellationRequested();
            return new(frozen.dependencies, frozen.ProfileSha256, frozen.modelId, frozen.budget, frozen.origins,
                frozen.HistoryOrder.ToArray(), frozen.Chunks.ToArray(), 3, frozen.originalDisclosure, selection);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { throw Unavailable(); }
    }

    private static string HostV3Identity(GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain, byte[] original)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write("aioffice-group-original-host-input-v3");
        var manifest = GroupWorkDependencyManifest.Create(preparation.Context, brain, preparation.BatchId);
        writer.Write(manifest.Length); writer.Write(manifest); writer.Write(original.Length); writer.Write(original); writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    // Reconstruct only the persisted selection and partition. No new TOP-N,
    // empty-history fallback, replacement input or recycled attempt is allowed.
    internal void RequireRestored(GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain,
        string profileSha256, IEnumerable<GroupBrainRequestOriginMetadata> originRows, CancellationToken token = default)
    {
        var restored = WireFormatVersion == 2
            ? Create(preparation, brain, profileSha256, modelId, budget, originRows, token)
            : RecreateV3(preparation, brain, profileSha256, modelId, budget, originRows, originalDisclosure!, null, token);
        if (!CryptographicOperations.FixedTimeEquals(Encode(), restored.Encode())) throw Unavailable();
    }

    // Reapply the stored whole history order to fresh reader results. The
    // public reader groups requests and glossary, while the original trusted
    // selection may interleave them. Never use cached original plaintext or
    // substitute newly selected history. This is a structural recovery only;
    // the caller still owns current fences, SQL plan authenticity and release.
    internal IReadOnlyList<RestoredChunk> RestoreChunks(GroupBatchSourcePreparation preparation,
        GroupBrainPrivateContext currentBrain, string profileSha256,
        IEnumerable<GroupBrainRequestOriginMetadata> currentOrigins, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparation); ArgumentNullException.ThrowIfNull(currentBrain);
        ArgumentNullException.ThrowIfNull(currentOrigins); token.ThrowIfCancellationRequested();
        try
        {
            if (!ReferenceEquals(preparation.Context.Handle, currentBrain.Handle)) throw Unavailable();
            var current = GroupWorkDependencyManifest.Create(preparation.Context, currentBrain, preparation.Context.BatchId);
            if (!CryptographicOperations.FixedTimeEquals(current, dependencies)) throw Unavailable();
            var snapshots = currentBrain.Snapshots.ToDictionary(x => (x.Kind, x.RecordId));
            var items = currentBrain.Items.ToDictionary(x => (x.Kind, x.RecordId));
            var ordered = new GroupBrainPrivateContext(currentBrain.Handle,
                HistoryOrder.Select(x => snapshots[(x.Kind, x.RecordId)]).ToArray(),
                HistoryOrder.Select(x => items[(x.Kind, x.RecordId)]).ToArray());
            RequireRestored(preparation, ordered, profileSha256, currentOrigins, token);
            var partition = WireFormatVersion == 2
                ? GroupAutomaticContextChunks.Create(preparation, ordered, modelId, budget, token)
                : GroupAutomaticContextChunks.RestoreV2(preparation, ordered,
                    GroupHistorySelectionDisclosure.RestoreOriginal(originalDisclosure!, preparation, ordered,
                        Dependencies, HistoryOrder, token), modelId, budget, token);
            if (partition.Count != Chunks.Count) throw Unavailable();
            var result = partition.Select((chunk, index) => new RestoredChunk(Chunks[index], chunk, ordered)).ToArray();
            token.ThrowIfCancellationRequested(); return Array.AsReadOnly(result);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw Unavailable(); }
    }

    internal sealed class RestoredChunk(Chunk original, GroupAutomaticContextChunk current, GroupBrainPrivateContext brain)
    {
        internal Chunk Original { get; } = original;
        internal GroupAutomaticContextChunk Current { get; } = current;
        internal GroupBrainPrivateContext Brain { get; } = brain;
        public override string ToString() => "Group restored original chunk (private content).";
    }

    internal byte[] Encode() => Write(true);

    private byte[] Write(bool includeOperations)
    {
        using var stream = new MemoryStream(MaximumBytes);
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(WireFormatVersion == 2 ? Magic : MagicV3);
        foreach (var version in Versions(WireFormatVersion)) Text(version);
        writer.Write(dependencies.Length); writer.Write(dependencies);
        if (WireFormatVersion == 3)
        {
            if (originalDisclosure is null || originalDisclosure.Length > GroupHistorySelectionDisclosure.MaximumOriginalBytes) throw Unavailable();
            writer.Write((ushort)originalDisclosure.Length); writer.Write(originalDisclosure);
        }
        writer.Write((byte)HistoryOrder.Count);
        foreach (var member in HistoryOrder)
        { writer.Write((byte)member.Kind); writer.Write(member.RecordId.ToByteArray()); }
        writer.Write(Convert.FromHexString(ProfileSha256)); Text(modelId); writer.Write(budget);
        writer.Write((byte)origins.Length);
        foreach (var origin in origins)
        {
            writer.Write(origin.RequestId.ToByteArray()); writer.Write(origin.OriginBatchId.ToByteArray());
            writer.Write(origin.OriginOperationId.ToByteArray()); writer.Write(origin.OriginCandidateOrdinal);
            writer.Write(origin.CreatedAtUtc.UtcTicks);
        }
        writer.Write((byte)Chunks.Count);
        foreach (var chunk in Chunks)
        {
            writer.Write((includeOperations ? chunk.OperationId : Guid.Empty).ToByteArray());
            writer.Write((byte)(chunk.RequiresModel ? 1 : 0)); writer.Write(Convert.FromHexString(chunk.InputSha256));
            writer.Write((byte)chunk.Members.Count);
            foreach (var member in chunk.Members)
            { writer.Write(member.MessageId.ToByteArray()); writer.Write(member.Revision); writer.Write(member.CommittedSequence); }
        }
        writer.Flush();
        if (stream.Length > MaximumBytes) throw Unavailable();
        return stream.ToArray();
        void Text(string text)
        {
            var bytes = Utf8.GetBytes(text);
            if (bytes.Length > 256) throw Unavailable();
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
    }

    internal static GroupAutomaticInputPlan Read(byte[]? value)
    {
        if (value is null || value.Length is < 256 or > MaximumBytes) throw Unavailable();
        try
        {
            using var stream = new MemoryStream(value.ToArray(), false);
            using var reader = new BinaryReader(stream, Utf8, true);
            var magic = Bytes(Magic.Length);
            var format = magic.AsSpan().SequenceEqual(Magic) ? 2 : magic.AsSpan().SequenceEqual(MagicV3) ? 3 : throw Unavailable();
            foreach (var version in Versions(format)) if (Text() != version) throw Unavailable();
            var length = reader.ReadInt32();
            if (length is < GroupWorkDependencyManifest.MinimumBytes or > GroupWorkDependencyManifest.MaximumBytes) throw Unavailable();
            var dependencies = Bytes(length); var manifest = GroupWorkDependencyManifest.Read(dependencies);
            if (manifest.OperationId != manifest.BatchId) throw Unavailable();
            byte[]? originalDisclosure = null;
            if (format == 3)
            {
                var size = reader.ReadUInt16(); if (size > GroupHistorySelectionDisclosure.MaximumOriginalBytes) throw Unavailable();
                originalDisclosure = Bytes(size);
            }
            var historyCount = reader.ReadByte();
            if (historyCount > GroupBrainCurrentReader.MaximumSelectedRevisions) throw Unavailable();
            var historyOrder = new HistoryMember[historyCount];
            for (var i = 0; i < historyOrder.Length; i++)
                historyOrder[i] = new((GroupBrainContentKind)reader.ReadByte(), Id());
            RequireHistory(manifest, historyOrder);
            if (format == 3) GroupHistorySelectionDisclosure.ValidateOriginal(originalDisclosure, manifest, historyOrder);
            var profile = Convert.ToHexString(Bytes(32)); var model = Text(); var budget = reader.ReadInt32();
            var originCount = reader.ReadByte();
            if (originCount > GroupBrainCurrentReader.MaximumSelectedRevisions) throw Unavailable();
            var origins = new GroupBrainRequestOriginMetadata[originCount];
            for (var i = 0; i < origins.Length; i++)
                origins[i] = new(manifest.Scope, Id(), Id(), Id(), reader.ReadInt32(), new(reader.ReadInt64(), TimeSpan.Zero));
            var selected = SelectOrigins(manifest, origins, CancellationToken.None);
            if (!selected.SequenceEqual(origins)) throw Unavailable();
            var chunkCount = reader.ReadByte(); if (chunkCount is < 1 or > 100) throw Unavailable();
            var chunks = new Chunk[chunkCount]; var sources = manifest.Sources.ToDictionary(x => x.MessageId);
            var seen = new HashSet<Guid>(); var sequences = new HashSet<long>(); Member? previous = null;
            for (var i = 0; i < chunks.Length; i++)
            {
                var operation = Id(); var kind = reader.ReadByte(); if (kind > 1) throw Unavailable();
                var input = Convert.ToHexString(Bytes(32)); var count = reader.ReadByte(); if (count is < 1 or > 100) throw Unavailable();
                var members = new Member[count];
                for (var j = 0; j < members.Length; j++)
                {
                    var member = new Member(Id(), reader.ReadInt64(), reader.ReadInt64());
                    if (!sources.TryGetValue(member.MessageId, out var source) || source.Revision != member.Revision
                        || !seen.Add(member.MessageId) || member.CommittedSequence <= 0 || member.CommittedSequence > manifest.AllocatedThroughSequence
                        || !sequences.Add(member.CommittedSequence) || previous is not null && member.CommittedSequence <= previous.CommittedSequence) throw Unavailable();
                    members[j] = member; previous = member;
                }
                chunks[i] = new(i + 1, operation, kind == 1, input, members);
            }
            var hasModel = chunks.Any(x => x.RequiresModel);
            if (stream.Position != value.Length || seen.Count != sources.Count
                || hasModel && (string.IsNullOrWhiteSpace(model) || model != model.Trim() || model.Any(char.IsControl)
                    || budget is < 1 or > GroupAutomaticModelContext.MaximumEstimatedTokenBudget)
                || !hasModel && (model != "" || budget != 0)) throw Unavailable();
            var plan = new GroupAutomaticInputPlan(dependencies, profile, model, budget, origins, historyOrder, chunks, format, originalDisclosure);
            var seed = SHA256.HashData(plan.Write(false));
            if (chunks.Any(x => x.OperationId != Operation(seed, x.Ordinal))
                || chunks.Select(x => x.OperationId).Distinct().Count() != chunks.Length
                || !CryptographicOperations.FixedTimeEquals(plan.Encode(), value)) throw Unavailable();
            return plan;

            byte[] Bytes(int count)
            {
                if (count > stream.Length - stream.Position) throw Unavailable();
                var result = reader.ReadBytes(count); if (result.Length != count) throw Unavailable(); return result;
            }
            Guid Id() => new Guid(Bytes(16)) is var id && id != Guid.Empty ? id : throw Unavailable();
            string Text()
            { var count = reader.ReadUInt16(); if (count > 256) throw Unavailable(); return Utf8.GetString(Bytes(count)); }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or EndOfStreamException or OverflowException)
        { throw Unavailable(); }
    }

    private static void RequireHistory(GroupWorkDependencyManifest manifest, IReadOnlyList<HistoryMember> history)
    {
        if (history.Count > GroupBrainCurrentReader.MaximumSelectedRevisions || history.Count != manifest.Dependencies.Count)
            throw Unavailable();
        var remaining = manifest.Dependencies.Select(x => (x.Kind, x.RecordId)).ToHashSet();
        foreach (var member in history)
            if (member.Kind is not (GroupBrainContentKind.RequestRevision or GroupBrainContentKind.GlossaryRevision)
                || member.RecordId == Guid.Empty || !remaining.Remove((member.Kind, member.RecordId))) throw Unavailable();
        if (remaining.Count != 0) throw Unavailable();
    }

    private static GroupBrainRequestOriginMetadata[] SelectOrigins(GroupWorkDependencyManifest manifest,
        IEnumerable<GroupBrainRequestOriginMetadata> rows, CancellationToken token)
    {
        var requested = manifest.Dependencies.Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId).ToHashSet();
        var origins = new Dictionary<Guid, GroupBrainRequestOriginMetadata>();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (origins.Count >= GroupBrainCurrentReader.MaximumSelectedRevisions || row is null || row.Scope != manifest.Scope
                || !requested.Contains(row.RequestId) || row.OriginBatchId == Guid.Empty || row.OriginBatchId == manifest.BatchId
                || row.OriginOperationId == Guid.Empty || row.OriginCandidateOrdinal is < 1 or > GroupAutomaticNotePlan.MaximumNotes
                || row.CreatedAtUtc.Offset != TimeSpan.Zero || row.RequestId != RequestIdentity(row)
                || !origins.TryAdd(row.RequestId, row)) throw Unavailable();
        }
        if (origins.Count != requested.Count) throw Unavailable();
        return origins.Values.OrderBy(x => x.RequestId).ToArray();
    }
    private static Guid RequestIdentity(GroupBrainRequestOriginMetadata row) => new(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
        $"aioffice-group-note-id-v1/{row.Scope.TenantId:D}/{row.Scope.CompanyId:D}/{row.Scope.SourceBindingId:D}/{row.OriginBatchId:D}/{row.OriginOperationId:D}/request/{row.OriginCandidateOrdinal}"))).AsSpan(0, 16));
    private static Guid Operation(byte[] seed, int ordinal)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write("aioffice-group-original-chunk-v1"); writer.Write(seed); writer.Write(ordinal); writer.Flush();
        return new(SHA256.HashData(stream.ToArray()).AsSpan(0, 16));
    }
    // Immutable wire labels. Current safety and authority fences still apply.
    private static string[] Versions(int format) => [format == 2 ? "group-automatic-context-v1" : "group-automatic-context-v2",
        "utf8-byte-estimate-v1", "group-secret-quarantine-v1", "group-work-proposal-v1", "group-model-attempt-budget-v1",
        "32D0403401CE4E3498DEA3D2CC68B0EC275E0641DBD695E48248EBCA9EC8E4DE"];
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(x => "0123456789ABCDEF".Contains(x));
    private static InvalidOperationException Unavailable() => new("Group original input plan is not available.");
}
