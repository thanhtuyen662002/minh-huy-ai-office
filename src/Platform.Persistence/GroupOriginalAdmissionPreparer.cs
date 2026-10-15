using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Fixed trusted composition dependency, never supplied by a request/DTO. This
// prepares historical original provenance; it appends no journal and calls no
// provider. SQL original/source/brain/claim/permission fences remain mandatory.
internal sealed class GroupOriginalAdmissionPreparer(IAiStructuredRequestAdmission admission, TimeProvider clock)
{
    private readonly object issuer = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly TimeSpan MaximumPreparation = TimeSpan.FromMinutes(2);

    internal async Task<PreparedOriginal> PrepareAsync(GroupAutomaticInputPlan original,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain,
        IEnumerable<GroupBrainRequestOriginMetadata> currentOrigins, CancellationToken caller = default)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(brain); ArgumentNullException.ThrowIfNull(currentOrigins);
        caller.ThrowIfCancellationRequested();
        var began = clock.GetTimestamp(); var beganUtc = UtcNow();
        var expires = beganUtc.Add(MaximumPreparation);
        if (expires > preparation.Context.Handle.Receipt.ExpiresAtUtc) expires = preparation.Context.Handle.Receipt.ExpiresAtUtc;
        if (expires <= beganUtc || admission is null) throw Unavailable();
        using var timeout = new CancellationTokenSource(expires - beganUtc, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timeout.Token);
        try
        {
            var origins = currentOrigins.Take(GroupBrainCurrentReader.MaximumSelectedRevisions + 1).ToArray();
            if (origins.Length > GroupBrainCurrentReader.MaximumSelectedRevisions) throw Unavailable();
            original.RequireOriginalIssuance(preparation, brain, origins, linked.Token);
            var restored = original.RestoreChunks(preparation, brain, original.ProfileSha256, origins, linked.Token);
            var snapshots = new AiStructuredAdmissionSnapshot?[restored.Count];
            string? profileIdentity = null;
            foreach (var chunk in restored)
            {
                RequireTime();
                if (chunk.Current.ModelContext is not { } context) continue;
                // The stable original operation supplies this measurement-only
                // request identity. A future actual attempt uses its journal ID.
                var request = context.CreateGatewayRequest(chunk.Original.OperationId);
                var handle = await admission.PrepareAsync(request, linked.Token).ConfigureAwait(false);
                RequireTime();
                var snapshot = admission.DescribePrepared(handle, linked.Token);
                RequireTime(); RequireRequest(snapshot, request, context.Manifest.ModelId);
                var identity = ProfileIdentity(snapshot);
                if (profileIdentity is not null && profileIdentity != identity) throw Unavailable();
                profileIdentity = identity; snapshots[chunk.Original.Ordinal - 1] = snapshot;
            }
            original.RequireOriginalIssuance(preparation, brain, origins, linked.Token);
            var sidecar = GroupOriginalAdmissionSidecar.Create(original, preparation.Context.Handle, snapshots);
            var issued = new PreparedOriginal(issuer, original, preparation, brain, sidecar, began, beganUtc, expires);
            RequireTime(); return issued;

            void RequireTime()
            {
                var elapsed = clock.GetElapsedTime(began); var now = UtcNow();
                caller.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested || elapsed < TimeSpan.Zero || elapsed >= expires - beganUtc
                    || now < beganUtc || now >= expires) throw Unavailable();
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Unavailable(); }
    }

    internal void RequireIssued(PreparedOriginal issued, GroupBatchSourcePreparation preparation,
        GroupBrainPrivateContext brain, CancellationToken caller = default)
    {
        if (InspectIssuedLocked(issued, preparation, brain, caller) is not null) throw Unavailable();
    }

    // The SQL owner must preserve this exact observation across rollback or
    // a later UTC rewind. It is not a provider or current-claim capability.
    internal GroupBatchClaimExpiryObservation? InspectIssuedLocked(PreparedOriginal issued,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain, CancellationToken caller = default)
    {
        caller.ThrowIfCancellationRequested();
        if (issued is null) throw Unavailable();
        issued.RequireOwner(issuer, preparation, brain);
        var elapsed = clock.GetElapsedTime(issued.Started); var now = UtcNow();
        caller.ThrowIfCancellationRequested();
        var handle = preparation.Context.Handle;
        if (now >= handle.Receipt.ExpiresAtUtc)
            return new GroupBatchClaimExpiryObservation(handle, now);
        if (elapsed < TimeSpan.Zero || elapsed >= issued.ExpiresAtUtc - issued.StartedAtUtc
            || now < issued.StartedAtUtc || now >= issued.ExpiresAtUtc) throw Unavailable();
        return null;
    }

    private static void RequireRequest(AiStructuredAdmissionSnapshot snapshot, AiGatewayRequest request, string expectedModel)
    {
        var input = Utf8.GetBytes(request.Input); var schema = Utf8.GetBytes(request.ResponseSchema!);
        try
        {
            if (snapshot is null || snapshot.Routes[0].ConfiguredModel != expectedModel
                || snapshot.InputBytes != input.Length || snapshot.SchemaBytes != schema.Length
                || snapshot.InputSha256 != Convert.ToHexString(SHA256.HashData(input))
                || snapshot.SchemaSha256 != Convert.ToHexString(SHA256.HashData(schema))) throw Unavailable();
        }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(schema); }
    }

    private static string ProfileIdentity(AiStructuredAdmissionSnapshot snapshot)
    {
        // Excludes plan/operation/input identity: never introduce a circular
        // V3ProfileSha -> sidecar -> PlanSha/operation fingerprint dependency.
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write("group-original-admission-profile-set-v1"); writer.Write(snapshot.GatewaySha256);
        writer.Write(snapshot.TokenizerVersion); writer.Write(snapshot.Routes.Count);
        foreach (var route in snapshot.Routes) { writer.Write(route.Ordinal); writer.Write(route.CandidateSha256); }
        writer.Flush(); return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal sealed class PreparedOriginal(object owner, GroupAutomaticInputPlan original,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain, GroupOriginalAdmissionSidecar sidecar,
        long started, DateTimeOffset startedAtUtc, DateTimeOffset expiresAtUtc)
    {
        internal GroupAutomaticInputPlan Original { get; } = original;
        internal GroupOriginalAdmissionSidecar Sidecar { get; } = sidecar;
        internal long Started { get; } = started;
        internal DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        internal DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        internal void RequireOwner(object expected, GroupBatchSourcePreparation currentPreparation, GroupBrainPrivateContext currentBrain)
        {
            if (!ReferenceEquals(owner, expected) || !ReferenceEquals(preparation, currentPreparation)
                || !ReferenceEquals(brain, currentBrain)) throw Unavailable();
            Original.RequireOriginalCarrier(preparation, brain); Sidecar.RequirePlan(Original);
        }
        public override string ToString() => "Prepared group original admission (SQL persistence still required).";
    }
    public override string ToString() => "Group original admission preparer (host-owned).";
    private DateTimeOffset UtcNow() { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }
    private static InvalidOperationException Unavailable() => GroupOriginalAdmissionSidecar.Unavailable();
}
