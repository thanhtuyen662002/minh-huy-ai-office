using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Continues only the owned, completed three-attempt journal fixture. No
// provider, model interpretation, IT notification or customer send occurs.
internal static class GroupExtractionFailureRuntimeProof
{
    internal const string Success = "PASS owned extraction failure SQL exact exhausted input wrong input refused configured key expiry witness rewind denied replacement host only protected three notes four metadata evidence Attention quarantine atomic NotesCommitted replay new nonce refused unchanged journal no provider model or IT send";

    internal static async Task RunAsync(GroupScope scope, Guid operation, GroupExtractionWorkerBinding worker,
        DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        await using var db = new PlatformDbContext(options);
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var previous = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id)
            .OrderByDescending(x => x.Epoch).FirstAsync(token);
        var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        if (previous.Epoch != 3 || state.Epoch != 3 || state.ExpiryObservedAtUtc != previous.ExpiresAtUtc) throw new InvalidOperationException();
        var clock = new GroupNoteRuntimeProof.OwnedClock(previous.ExpiresAtUtc.AddTicks(1));
        var originalJournal = await JournalDigestAsync();
        if (await db.GroupModelAttemptStarts.CountAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token) != 3
            || await db.GroupModelAttemptFailures.CountAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token) != 2) throw new InvalidOperationException();
        var effectOperation = Guid.NewGuid();
        var first = await AcquireAsync(4);
        var pending = await PrepareAsync(first.CurrentHandle!);
        var wrong = GroupAutomaticModelContext.Create(pending.Input.Preparation, pending.Input.Brain, "owned-other-input", 64000);
        await RefusedAsync(() => pending.Writer.CommitExtractionFailureAsync(wrong, effectOperation, token));
        if (pending.Keys.Writes != 0 || await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, effectOperation, token) != 0)
            throw new InvalidOperationException();
        // The external key await cannot carry a prior exhaustion observation
        // over an expired claim. Only the durable expiry witness may remain.
        pending.Keys.BeforeWrite = () => clock.Current = first.Receipt.ExpiresAtUtc;
        await RefusedAsync(() => pending.Writer.CommitExtractionFailureAsync(pending.Input, effectOperation, token));
        var expired = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        if (expired.ExpiryObservedAtUtc != first.Receipt.ExpiresAtUtc || pending.Keys.Writes != 1
            || await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, effectOperation, token) != 0
            || await JournalDigestAsync() != originalJournal || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        await GroupAutomaticRawRuntimeProof.RequireEmptyAsync(db, scope, token);
        clock.Current = first.Receipt.IssuedAtUtc;
        await RefusedAsync(() => pending.Writer.CommitExtractionFailureAsync(pending.Input, effectOperation, token));
        if (pending.Keys.Writes != 1 || await JournalDigestAsync() != originalJournal) throw new InvalidOperationException();

        clock.Current = first.Receipt.ExpiresAtUtc.AddTicks(1);
        var replacement = await AcquireAsync(5);
        var current = await PrepareAsync(replacement.CurrentHandle!);
        if (GroupModelAttemptBudget.InputIdentity(current.Input) != GroupModelAttemptBudget.InputIdentity(pending.Input)) throw new InvalidOperationException();
        var committed = await current.Writer.CommitExtractionFailureAsync(current.Input, effectOperation, token);
        if (committed.WasAlreadyCommitted || committed.RequestIds.Count != 3 || current.Keys.Writes != 1
            || await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, effectOperation, token) != 17) throw new InvalidOperationException();
        await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, effectOperation, 2, token);
        _ = await GroupAutomaticManifestRuntimeProof.RequireAsync(db, scope, effectOperation, token);
        _ = await GroupAutomaticEffectExpectationRuntimeProof.RequireAsync(db, scope, effectOperation, token);
        var requests = await db.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OriginOperationId == effectOperation).ToArrayAsync(token);
        if (requests.Length != 3 || requests.Any(x => x.Kind != GroupNoteKind.NeedsClarification
            || x.BusinessStatus != GroupNoteBusinessStatus.NeedsClarification || x.AssignedToUserId is not null
            || x.CommittedDueAtUtc is not null || x.ConfirmedByUserId is not null || x.ConfirmedAtUtc is not null)) throw new InvalidOperationException();
        var ids = requests.Select(x => x.Id).ToArray();
        var revisions = await db.GroupRequestRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && ids.Contains(x.RequestId)).ToArrayAsync(token);
        if (revisions.Length != 3 || revisions.Any(x => x.Revision != 1 || x.Origin != GroupRequestRevisionOrigin.HostAttention
            || x.VerificationLevel != GroupRequestVerificationLevel.HostObserved || x.AuthorUserId is not null
            || x.AuthorServiceId != worker.ServiceId || x.ClaimEpoch != 5)) throw new InvalidOperationException();
        var protector = new GroupBrainContentProtector(); var reasons = new HashSet<GroupHostAttentionReason>();
        foreach (var revision in revisions)
        {
            using var key = await current.Keys.ResolveReadAsync(scope, revision.ContentKeyId, token);
            var clear = protector.Unprotect(new(scope, GroupBrainContentKind.RequestRevision, revision.RequestId, 1,
                revision.SourceVersion, revision.DeletionGeneration), revision.ProtectedContent, key.Key, key.KeyId);
            var note = GroupBrainPayloadCodec.DecodeHostAttention(clear);
            if (!reasons.Add(note.Reason) || note.HasCoverageGap || clear.Contains("Password", StringComparison.Ordinal)
                || clear.Contains("Tra cứu", StringComparison.Ordinal)) throw new InvalidOperationException();
            var expected = current.Input.Preparation.Receipts.Where(x => note.Reason switch
            {
                GroupHostAttentionReason.UnsupportedMedia => x.HasUnsupportedMedia,
                GroupHostAttentionReason.SecretQuarantine => x.Disposition == GroupSourcePreparationDisposition.Quarantined,
                GroupHostAttentionReason.ExtractionFailed => x.Disposition == GroupSourcePreparationDisposition.ModelText,
                _ => false
            }).Select(x => new GroupHostAttentionReference(x.MessageId, x.Revision)).ToHashSet();
            if (note.SourceReferences.Count != expected.Count || !note.SourceReferences.ToHashSet().SetEquals(expected)) throw new InvalidOperationException();
        }
        if (!reasons.SetEquals([GroupHostAttentionReason.UnsupportedMedia, GroupHostAttentionReason.SecretQuarantine,
            GroupHostAttentionReason.ExtractionFailed])) throw new InvalidOperationException();
        var evidence = await db.GroupRequestEvidence.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && ids.Contains(x.RequestId)).ToArrayAsync(token);
        if (evidence.Length != 4 || evidence.Any(x => x.Kind != GroupRequestEvidenceKind.HostMetadataAttention)) throw new InvalidOperationException();
        var dispositions = await db.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == effectOperation).ToArrayAsync(token);
        if (dispositions.Length != 2 || dispositions.Count(x => x.Outcome == GroupWorkSourceOutcome.Attention) != 1
            || dispositions.Count(x => x.Outcome == GroupWorkSourceOutcome.Quarantined) != 1) throw new InvalidOperationException();
        var outbox = await db.GroupNotesCommittedOutbox.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == effectOperation, token);
        if (outbox.NoteCount != 3 || outbox.PublishedAtUtc is not null || outbox.PublishAttempts != 0) throw new InvalidOperationException();
        var graph = await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, effectOperation, token);
        var replay = await current.Writer.CommitExtractionFailureAsync(current.Input, effectOperation, token);
        if (!replay.WasAlreadyCommitted || replay.OutboxId != committed.OutboxId
            || !replay.RequestIds.SequenceEqual(committed.RequestIds)) throw new InvalidOperationException();
        await RefusedAsync(() => current.Writer.CommitExtractionFailureAsync(current.Input, Guid.NewGuid(), token));
        if (await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, effectOperation, token) != graph
            || await JournalDigestAsync() != originalJournal || db.ChangeTracker.HasChanges()
            || db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed) throw new InvalidOperationException();
        await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, effectOperation, 2, token);
        Console.WriteLine(Success);

        async Task<GroupBatchClaimResult> AcquireAsync(long epoch)
        {
            var result = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, allocation.Id,
                Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
            if (result.WasAlreadyClaimed || result.CurrentHandle is null || result.Receipt.Epoch != epoch) throw new InvalidOperationException();
            return result;
        }
        async Task<(GroupAutomaticModelContext Input, GroupNoteCommitStore Writer, GroupNoteRuntimeProof.CountedKeys Keys)> PrepareAsync(GroupBatchClaimHandle handle)
        {
            var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
            var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
            var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
            var selected = await db.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId)
                .OrderBy(x => x.CommittedSequence).Select(x => x.MessageId).ToArrayAsync(token);
            var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, selected, token));
            if (preparation.Receipts.Count != 2 || preparation.Candidates.Count != 1 || keys.Reads != 1 || keys.Writes != 0) throw new InvalidOperationException();
            var input = GroupAutomaticModelContext.Create(preparation, await brain.ReadAsync(handle, [], [], token), "owned-synthetic-journal", 64000);
            return (input, new(db, worker, clock, sources, brain, keys, new()), keys);
        }
        async Task<string> JournalDigestAsync()
        {
            var starts = await db.GroupModelAttemptStarts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.Ordinal).ToArrayAsync(token);
            var failures = await db.GroupModelAttemptFailures.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.RequestId).ToArrayAsync(token);
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object[] { starts, failures })));
        }
    }

    private static async Task RefusedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException) { return; }
        throw new InvalidOperationException();
    }
}
