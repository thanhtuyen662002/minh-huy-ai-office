using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed partial class GroupModelAttemptAdmissionPreparer
{
    // Called only after the fixed journal's complete current SQL fence has
    // committed and disposed its transaction. A structural permit alone is
    // not SQL release authority; the journal owns that mechanical call path.
    internal async Task<AiGatewayResponse> ExecuteAfterCurrentJournalAsync(GroupModelAttemptPermit permit,
        CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        try
        {
            ArgumentNullException.ThrowIfNull(permit); RequireOutsideUnit();
            var issued = permit.Admission ?? throw Unavailable();
            if (!ReferenceEquals(permit.Original, issued.Original) || !ReferenceEquals(permit.Context, issued.Original.Context)
                || permit.Start.RequestId != issued.RequestId || permit.Start.Scope != permit.Context.Scope
                || permit.Start.BatchId != permit.Context.BatchId || permit.Start.InputSha256 != GroupModelAttemptBudget.InputIdentity(permit.Context)
                || permit.Start.StartedAtUtc < issued.StartedAtUtc || permit.Start.DeadlineUtc > issued.ExpiresAtUtc
                || permit.Start.DeadlineUtc <= permit.Start.StartedAtUtc) throw Unavailable();
            RequireRemaining();
            var handle = issued.ConsumeForExecution(issuer, permit.Context);
            // One use even on current-catalog rejection, provider failure or
            // lost reply. The actual existing gate also consumes its handle.
            var snapshot = admission.DescribePrepared(handle, caller);
            RequireOutsideUnit();
            if (snapshot is null || snapshot.SnapshotSha256 != issued.Snapshot.SnapshotSha256) throw Unavailable();
            using var timeout = new CancellationTokenSource(RequireRemaining(), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timeout.Token);
            var response = await admission.ExecutePreparedAsync(handle, linked.Token).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested(); RequireOutsideUnit();
            _ = RequireRemaining();
            if (timeout.IsCancellationRequested) throw Unavailable();
            response.ValidateFor(permit.Context.CreateGatewayRequest(issued.RequestId));
            return response;

            TimeSpan RequireRemaining()
            {
                if (InspectIssuedLocked(issued, permit.Context, caller) is { } observation)
                    throw new GroupAdmittedExecutionExpiry(issuer, observation);
                var now = UtcNow();
                if (now >= permit.Context.Preparation.Context.Handle.Receipt.ExpiresAtUtc)
                    throw new GroupAdmittedExecutionExpiry(issuer, new(permit.Context.Preparation.Context.Handle, now));
                if (now < permit.Start.StartedAtUtc || now >= permit.Start.DeadlineUtc) throw Unavailable();
                var remaining = permit.Start.DeadlineUtc - now;
                var monotonic = issued.ExpiresAtUtc - issued.StartedAtUtc - clock.GetElapsedTime(issued.Started);
                if (monotonic <= TimeSpan.Zero) throw Unavailable();
                return remaining < monotonic ? remaining : monotonic;
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (GroupAdmittedExecutionExpiry error) when (permit?.Admission is { } prepared
            && error.IsIssuedBy(issuer, prepared.Original.Context))
        { throw; }
        catch (Exception error) when (error is IAiGatewayProviderFailure and not OperationCanceledException)
        {
            GroupModelAttemptFailure failure;
            try
            {
                if (!GroupModelAttemptProviderFailure.TryClassify(error, out failure, caller)) throw Unavailable();
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
            catch (Exception classificationError) when (classificationError is not OutOfMemoryException) { throw Unavailable(); }
            throw new GroupAdmittedProviderFailure(failure == GroupModelAttemptFailure.TransientProvider);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Unavailable(); }
    }

    internal void RequireExecutionExpiry(GroupAdmittedExecutionExpiry expiry, GroupModelAttemptPermit permit, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (permit.Admission is not { } prepared || !ReferenceEquals(prepared.Original, permit.Original)
            || !ReferenceEquals(prepared.Original.Context, permit.Context) || !expiry.IsIssuedBy(issuer, permit.Context)) throw Unavailable();
        prepared.RequireOwner(issuer, permit.Context);
    }
}

// Preserve only the existing host-owned typed retry classification across
// the layer boundary. Never retain provider Message/ToString/InnerException.
internal sealed class GroupAdmittedProviderFailure(bool transient)
    : Exception("AI group model execution was not confirmed."), IAiGatewayProviderFailure
{
    public bool IsTransient { get; } = transient;
}

internal sealed class GroupAdmittedExecutionExpiry(object owner, GroupBatchClaimExpiryObservation observation)
    : Exception("Group admitted model claim expired.")
{
    internal GroupBatchClaimExpiryObservation Observation { get; } = observation;
    internal bool IsIssuedBy(object expected, GroupAutomaticModelContext context) => ReferenceEquals(owner, expected)
        && ReferenceEquals(Observation.Handle, context.Preparation.Context.Handle) && Observation.ObservedAtUtc.Offset == TimeSpan.Zero
        && Observation.ObservedAtUtc >= Observation.Handle.Receipt.ExpiresAtUtc;
}
