using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Fixed host dependency for a NEW attempt on restored original input. The
// historical sidecar is data, never current model release or SQL authority.
// This step executes no provider and commits no journal. The journal must
// authenticate the original/sidecar in its own transaction before any permit.
internal sealed class GroupModelAttemptAdmissionPreparer(PlatformDbContext database,
    IAiStructuredRequestAdmission admission, TimeProvider clock)
{
    private readonly object issuer = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly TimeSpan PreparationBudget = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(2);

    internal bool UsesContext(PlatformDbContext current, TimeProvider time) =>
        ReferenceEquals(database, current) && ReferenceEquals(clock, time);

    internal async Task<PreparedAttempt> PrepareAsync(GroupAutomaticInputPlan original,
        GroupAutomaticInputPlan.RestoredChunk restored, GroupOriginalAdmissionSidecar historical,
        CancellationToken caller = default)
    {
        caller.ThrowIfCancellationRequested();
        try
        {
            ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(restored);
            ArgumentNullException.ThrowIfNull(historical); RequireOutsideUnit();
            var began = clock.GetTimestamp(); var beganUtc = UtcNow();
            var binding = GroupModelAttemptOriginalBinding.Create(original, restored, caller);
            if (binding.Plan.WireFormatVersion != 3) throw Unavailable();
            var sidecar = GroupOriginalAdmissionSidecar.Read(historical.Encode()); sidecar.RequirePlan(binding.Plan);
            var ordinal = restored.Original.Ordinal;
            var old = sidecar.Entries[ordinal - 1].Snapshot ?? throw Unavailable();
            var expires = beganUtc.Add(MaximumLifetime);
            var claim = binding.Context.Preparation.Context.Handle;
            if (expires > claim.Receipt.ExpiresAtUtc) expires = claim.Receipt.ExpiresAtUtc;
            if (expires <= beganUtc || admission is null) throw Unavailable();
            // The trusted host creates the nonce; neither source text nor
            // historical model/profile metadata supplies an invocation ID.
            var requestId = Guid.NewGuid();
            var request = binding.Context.CreateGatewayRequest(requestId);
            RequireRequest(old, request);
            var preparationTime = expires - beganUtc;
            if (preparationTime > PreparationBudget) preparationTime = PreparationBudget;
            using var timeout = new CancellationTokenSource(preparationTime, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timeout.Token);
            RequireTime();
            var handle = await admission.PrepareAsync(request, linked.Token).ConfigureAwait(false);
            RequireTime(); RequireOutsideUnit();
            if (handle is null) throw Unavailable();
            var snapshot = admission.DescribePrepared(handle, linked.Token);
            RequireTime(); RequireOutsideUnit(); RequireRequest(snapshot, request);
            // Current routes, tokenizer, windows and evidence may have changed.
            // The existing gate qualifies them. Preserve the original exact
            // input/schema; do not turn historical routes into a release grant.
            var issued = new PreparedAttempt(issuer, binding, sidecar, ordinal, requestId,
                snapshot, handle, began, beganUtc, expires);
            RequireTime(); return issued;

            void RequireTime()
            {
                var elapsed = clock.GetElapsedTime(began); var now = UtcNow();
                caller.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested || elapsed < TimeSpan.Zero || elapsed >= preparationTime
                    || now < beganUtc || now >= beganUtc.Add(preparationTime) || now >= expires) throw Unavailable();
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Unavailable(); }
    }

    // SQL-only fence: no gate/tokenizer/provider/key calls. The SQL owner must
    // preserve an authentic expiry observation across rollback and UTC rewind.
    // This validates issuance/lifetime only, not persistence/current authority.
    internal GroupBatchClaimExpiryObservation? InspectIssuedLocked(PreparedAttempt issued,
        GroupAutomaticModelContext context, CancellationToken caller = default)
    {
        caller.ThrowIfCancellationRequested();
        if (issued is null || context is null) throw Unavailable();
        issued.RequireOwner(issuer, context);
        var elapsed = clock.GetElapsedTime(issued.Started); var now = UtcNow();
        caller.ThrowIfCancellationRequested();
        var claim = context.Preparation.Context.Handle;
        if (now >= claim.Receipt.ExpiresAtUtc) return new(claim, now);
        if (elapsed < TimeSpan.Zero || elapsed >= issued.ExpiresAtUtc - issued.StartedAtUtc
            || now < issued.StartedAtUtc || now >= issued.ExpiresAtUtc) throw Unavailable();
        return null;
    }

    private void RequireOutsideUnit()
    {
        if (database is null || System.Transactions.Transaction.Current is not null
            || database.Database.CurrentTransaction is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
    }

    private static void RequireRequest(AiStructuredAdmissionSnapshot snapshot, AiGatewayRequest request)
    {
        var input = Utf8.GetBytes(request.Input); var schema = Utf8.GetBytes(request.ResponseSchema!);
        try
        {
            if (snapshot is null || snapshot.InputBytes != input.Length || snapshot.SchemaBytes != schema.Length
                || snapshot.InputSha256 != Convert.ToHexString(SHA256.HashData(input))
                || snapshot.SchemaSha256 != Convert.ToHexString(SHA256.HashData(schema))) throw Unavailable();
        }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(schema); }
    }

    internal sealed class PreparedAttempt(object owner, GroupModelAttemptOriginalBinding original,
        GroupOriginalAdmissionSidecar sidecar, int ordinal, Guid requestId, AiStructuredAdmissionSnapshot snapshot,
        IAiPreparedStructuredRequest handle, long started, DateTimeOffset startedAtUtc, DateTimeOffset expiresAtUtc)
    {
        internal GroupModelAttemptOriginalBinding Original { get; } = original;
        internal GroupOriginalAdmissionSidecar Sidecar { get; } = sidecar;
        internal int Ordinal { get; } = ordinal;
        internal Guid RequestId { get; } = requestId;
        internal AiStructuredAdmissionSnapshot Snapshot { get; } = snapshot;
        internal long Started { get; } = started;
        internal DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        internal DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        internal void RequireOwner(object expected, GroupAutomaticModelContext current)
        {
            if (!ReferenceEquals(owner, expected) || !ReferenceEquals(Original.Context, current)
                || handle is null || RequestId == Guid.Empty) throw Unavailable();
        }
        // Intentionally no provider-execution API before the durable companion
        // and exact committed permit are implemented. Retain the opaque handle
        // without exposing it as a substitute for that future SQL authority.
        public override string ToString() => "Prepared group model attempt (journal commit still required).";
    }
    public override string ToString() => "Group model attempt admission preparer (host-owned).";
    private DateTimeOffset UtcNow() { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }
    private static InvalidOperationException Unavailable() => new("Group model attempt admission is not available.");
}
