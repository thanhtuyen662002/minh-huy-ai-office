using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Structural journal policy only. These rows are not EF entities or a current
// SQL read. No public retry/exhaustion capability is created by this reducer.
internal sealed record GroupModelAttemptStarted(GroupScope Scope, Guid BatchId, string InputSha256,
    int Ordinal, Guid RequestId, Guid? PredecessorRequestId, Guid ClaimOperationId, long ClaimEpoch,
    DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineUtc);
internal enum GroupModelAttemptFailure { TransientProvider = 1, PermanentProvider = 2, InvalidOutput = 3, Interrupted = 4 }
internal sealed record GroupModelAttemptFailed(GroupScope Scope, Guid RequestId,
    GroupModelAttemptFailure Failure, DateTimeOffset ObservedAtUtc);
internal enum GroupModelAttemptDisposition { Available = 1, Wait = 2, Exhausted = 3 }
internal sealed record GroupModelAttemptBudgetDecision(GroupModelAttemptDisposition Disposition,
    int ConsumedAttempts, DateTimeOffset? RetryAtUtc);

internal static class GroupModelAttemptBudget
{
    internal const string Version = "group-model-attempt-budget-v1";
    internal const int MaximumAttempts = 3;
    internal static readonly TimeSpan MaximumAttemptLifetime = TimeSpan.FromMinutes(2);

    // Stable across claim replacement; binds the complete original dependency
    // set and exact model input/schema/accounting metadata. No plaintext is
    // retained in the identity. The model ID does not select a Gateway model.
    internal static string InputIdentity(GroupAutomaticModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.CreateGatewayRequest(context.BatchId);
        var dependencies = GroupWorkDependencyManifest.Create(context.Preparation.Context, context.Brain, context.BatchId);
        using var stream = new MemoryStream();
        try
        {
            using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
            writer.Write(Version); writer.Write(dependencies.Length); writer.Write(dependencies);
            writer.Write(GroupAutomaticModelContext.TokenAccountingVersion); writer.Write(context.Manifest.ModelId);
            writer.Write(context.Manifest.TokenBudget); writer.Write(context.Manifest.UsedTokens);
            writer.Write(request.Input); writer.Write(request.ResponseSchema!); writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        }
        finally { CryptographicOperations.ZeroMemory(stream.GetBuffer()); CryptographicOperations.ZeroMemory(dependencies); }
    }

    internal static GroupModelAttemptBudgetDecision Read(GroupScope scope, Guid batchId, string inputSha256,
        IEnumerable<GroupModelAttemptStarted> starts, IEnumerable<GroupModelAttemptFailed> failures,
        DateTimeOffset now, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(failures); token.ThrowIfCancellationRequested();
        try
        {
            scope.Validate();
            if (batchId == Guid.Empty || !Hash(inputSha256) || now.Offset != TimeSpan.Zero) throw Unavailable();
            var ordered = new List<GroupModelAttemptStarted>(MaximumAttempts);
            var requestIds = new HashSet<Guid>();
            foreach (var row in starts)
            {
                token.ThrowIfCancellationRequested();
                if (ordered.Count == MaximumAttempts || row is null || row.Scope != scope || row.BatchId != batchId
                    || row.InputSha256 != inputSha256 || row.Ordinal != ordered.Count + 1
                    || row.RequestId == Guid.Empty || !requestIds.Add(row.RequestId) || row.ClaimOperationId == Guid.Empty
                    || row.ClaimEpoch <= 0 || row.StartedAtUtc.Offset != TimeSpan.Zero || row.DeadlineUtc.Offset != TimeSpan.Zero
                    || row.StartedAtUtc > now || row.DeadlineUtc <= row.StartedAtUtc
                    || row.DeadlineUtc - row.StartedAtUtc > MaximumAttemptLifetime
                    || row.PredecessorRequestId != (ordered.Count == 0 ? null : ordered[^1].RequestId)) throw Unavailable();
                if (ordered.Count != 0 && row.StartedAtUtc < ordered[^1].StartedAtUtc) throw Unavailable();
                ordered.Add(row);
            }
            var byRequest = ordered.ToDictionary(x => x.RequestId);
            var failed = new Dictionary<Guid, GroupModelAttemptFailed>();
            foreach (var row in failures)
            {
                token.ThrowIfCancellationRequested();
                if (failed.Count == MaximumAttempts || row is null || row.Scope != scope || !Enum.IsDefined(row.Failure)
                    || !byRequest.TryGetValue(row.RequestId, out var start) || !failed.TryAdd(row.RequestId, row)
                    || row.ObservedAtUtc.Offset != TimeSpan.Zero || row.ObservedAtUtc < start.StartedAtUtc
                    || row.ObservedAtUtc > start.DeadlineUtc || row.ObservedAtUtc > now) throw Unavailable();
            }
            // Every successor must respect the preceding terminal observation
            // or original deadline and fixed backoff. An incomplete earlier
            // attempt cannot disappear just because a later row exists.
            for (var i = 1; i < ordered.Count; i++)
                if (NextAt(ordered[i - 1]) is not { } eligible || ordered[i].StartedAtUtc < eligible) throw Unavailable();
            token.ThrowIfCancellationRequested();
            if (ordered.Count == 0) return new(GroupModelAttemptDisposition.Available, 0, null);
            var last = ordered[^1];
            if (ordered.Count == MaximumAttempts)
                return failed.ContainsKey(last.RequestId) || now >= last.DeadlineUtc
                    ? new(GroupModelAttemptDisposition.Exhausted, ordered.Count, null)
                    : new(GroupModelAttemptDisposition.Wait, ordered.Count, last.DeadlineUtc);
            var next = NextAt(last);
            if (next is null) return new(GroupModelAttemptDisposition.Exhausted, ordered.Count, null);
            if (now < next) return new(GroupModelAttemptDisposition.Wait, ordered.Count, next);
            return new(GroupModelAttemptDisposition.Available, ordered.Count, null);

            DateTimeOffset? NextAt(GroupModelAttemptStarted start)
            {
                var outcome = failed.GetValueOrDefault(start.RequestId);
                if (outcome?.Failure == GroupModelAttemptFailure.PermanentProvider) return null;
                var observed = outcome?.ObservedAtUtc ?? start.DeadlineUtc;
                return observed.AddSeconds(start.Ordinal == 1 ? 15 : 60);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { throw Unavailable(); }
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(x => "0123456789ABCDEF".Contains(x));
    private static InvalidOperationException Unavailable() => new("Group model attempt journal is not available.");
}
