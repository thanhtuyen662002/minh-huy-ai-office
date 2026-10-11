using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Real shipping stores on a separately owned SQL fixture. Interpretations are
// fixed synthetic test inputs; this does not evaluate a model or raw completion.
internal static class GroupAutomaticNoteRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("automatic-note-prepare" or "automatic-note-expiry" or "automatic-note-key-expiry" or "automatic-note-commit"
            or "automatic-host-prepare" or "automatic-host-expiry" or "automatic-host-key-expiry" or "automatic-host-commit"))
            throw new InvalidOperationException();
        var hostOnly = mode.StartsWith("automatic-host-", StringComparison.Ordinal);
        var step = mode[(hostOnly ? "automatic-host-" : "automatic-note-").Length..];
        var expectedRows = hostOnly ? 9 : 21;
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var effect = new GroupNoteRuntimeProof.EffectEvidence(scope, clock) { ExpectedRows = expectedRows, ExpectedRawRows = 2 };
        var dependencyProof = new GroupAutomaticDependencyRuntimeProof.Evidence(scope);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new GroupNoteRuntimeProof.FlushProbe(effect), new GroupNoteRuntimeProof.RollbackProbe(effect),
                new GroupAutomaticDependencyRuntimeProof.ReadProbe(dependencyProof)).Options);
        var references = await db.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.CommittedSequence).ToArrayAsync(token);
        if (references.Length != 2 || references[0].Id != operation || references[0].CommittedSequence != 1
            || references[1].CommittedSequence != 2) throw new InvalidOperationException();
        if (step == "prepare")
        {
            var inbox = new GroupIngressInboxStore(db, worker, clock);
            foreach (var value in references)
                await inbox.ReceiveAsync(new(1, scope, value.Id, value.MessageId, value.Revision, value.CommittedSequence), token);
            var last = await db.GroupSourceStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.LastPendingAtUtc).SingleAsync(token)
                ?? throw new InvalidOperationException();
            clock.Current = last.AddMinutes(3);
            var allocation = await new GroupBatchAllocationStore(db, worker, new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)), clock)
                .AllocateDueAsync(scope, operation, token) ?? throw new InvalidOperationException();
            if (allocation.WasAlreadyAllocated || allocation.AfterSequence != 0 || allocation.AllocatedThroughSequence != 2
                || allocation.Revisions.Count != 2 || db.ChangeTracker.HasChanges()
                || await db.GroupBatchClaimStates.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                    && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
            await RequireEmptyEffectsAsync();
            Console.WriteLine(hostOnly
                ? "PASS owned automatic host actual inbox allocation two empty media sources no claims effects or model"
                : "PASS owned automatic note actual inbox and allocation exact two media references empty effects and claims");
            return;
        }
        var allocated = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var previous = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocated.Id)
            .OrderByDescending(x => x.Epoch).FirstOrDefaultAsync(token);
        var expectedEpoch = step switch { "expiry" => 1, "key-expiry" => 2, _ => 3 };
        if ((previous?.Epoch ?? 0) != expectedEpoch - 1) throw new InvalidOperationException();
        if (previous is not null)
        {
            var previousState = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocated.Id, token);
            if (previousState.Epoch != previous.Epoch || previousState.ExpiryObservedAtUtc != previous.ExpiresAtUtc)
                throw new InvalidOperationException();
            if (clock.Current <= previous.ExpiresAtUtc) clock.Current = previous.ExpiresAtUtc.AddTicks(1);
        }
        else if (clock.Current < allocated.AllocatedAtUtc) clock.Current = allocated.AllocatedAtUtc;
        await RequireEmptyEffectsAsync();
        var claim = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, allocated.Id, Guid.NewGuid(), Guid.NewGuid(),
            TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        if (claim.WasAlreadyClaimed || claim.CurrentHandle is null || claim.Receipt.Epoch != expectedEpoch) throw new InvalidOperationException();
        var handle = claim.CurrentHandle;
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, references.Select(x => x.MessageId).ToArray(), token));
        var source = preparation.Candidates.SingleOrDefault();
        if (preparation.Receipts.Count != 2) throw new InvalidOperationException();
        if (hostOnly)
        {
            if (source is not null || preparation.Receipts.Any(x => x.Disposition != GroupSourcePreparationDisposition.EmptyText
                || !x.HasUnsupportedMedia)) throw new InvalidOperationException();
        }
        else if (source is null || source.Kind != GroupSourceEventKind.Media || source.Text != "Tra cứu tồn kho 😀\uFEFF "
            || preparation.Receipts.Count(x => x.Disposition == GroupSourcePreparationDisposition.Quarantined) != 1) throw new InvalidOperationException();
        var proposal = hostOnly ? null : Proposal(false); var plan = GroupAutomaticNotePlan.Create(preparation, proposal);
        if (plan.HasCoverageGap || plan.SourceDispositions.Count != 2) throw new InvalidOperationException();
        if (hostOnly)
        {
            if (plan.AiNotes.Count != 0 || plan.HostNotes.Count != 1 || plan.NoteCount != 1
                || plan.HostNotes[0].Reason != GroupHostAttentionReason.UnsupportedMedia || plan.HostNotes[0].SourceReferences.Count != 2
                || plan.SourceDispositions.Any(x => x.Outcome != GroupWorkSourceOutcome.Attention || !x.HasHostAttention)) throw new InvalidOperationException();
        }
        else if (plan.AiNotes.Count != 2 || plan.HostNotes.Count != 2 || plan.NoteCount != 4
            || plan.HostNotes.Sum(x => x.SourceReferences.Count) != 3
            || plan.SourceDispositions.Count(x => x.Outcome == GroupWorkSourceOutcome.Work) != 1
            || plan.SourceDispositions.Count(x => x.Outcome == GroupWorkSourceOutcome.Quarantined) != 1) throw new InvalidOperationException();
        var dependencies = await brain.ReadAsync(handle, [], [], token);
        var effectOperation = Guid.NewGuid();
        var store = new GroupNoteCommitStore(db, worker, clock, sources, brain, keys, new());
        if (keys.Reads != 1 || keys.Writes != 0 || dependencies.Items.Count != 0 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        // Reuse the exact legacy expiry probe, with its fixed row expectation
        // expanded for this four-note graph. Witness classification is unchanged.
        effect.ObserveCommit(step switch { "expiry" => "note-expiry", "key-expiry" => "note-key-expiry", _ => "note-commit" },
            effectOperation, handle.Receipt.ExpiresAtUtc);
        effect.Claim = handle.Receipt;
        if (step is "expiry" or "key-expiry")
        {
            if (step == "key-expiry") keys.BeforeWrite = () => clock.Current = handle.Receipt.ExpiresAtUtc;
            await DeniedAsync(() => store.CommitAutomaticAsync(plan, dependencies, effectOperation, token));
            if (step == "expiry" && (!effect.StagedObserved || !effect.Flushed || !effect.RolledBack || effect.SavepointChecks < 1))
                throw new InvalidOperationException();
            if (step == "key-expiry" && (effect.StagedObserved || effect.Flushed || effect.RolledBack || effect.SavepointChecks != 0))
                throw new InvalidOperationException();
            await RequireEmptyEffectsAsync();
            var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocated.Id, token);
            if (state.Epoch != expectedEpoch || state.ExpiryObservedAtUtc != handle.Receipt.ExpiresAtUtc
                || keys.Reads != 1 || keys.Writes != 1) throw new InvalidOperationException();
            clock.Current = handle.Receipt.IssuedAtUtc;
            await DeniedAsync(() => store.CommitAutomaticAsync(plan, dependencies, effectOperation, token));
            await RequireEmptyEffectsAsync();
            if (keys.Reads != 1 || keys.Writes != 1) throw new InvalidOperationException();
            Console.WriteLine(hostOnly
                ? step == "expiry"
                    ? "PASS owned automatic host nine flushed SQL effects rollback source lock retained clean detach only expiry witness clock rollback denied"
                    : "PASS owned automatic host configured write key outside SQL expiry witness no effects clock rollback denied"
                : step == "expiry"
                    ? "PASS owned automatic note twenty-one flushed SQL effects rollback source lock retained clean detach only expiry witness clock rollback denied"
                    : "PASS owned automatic note configured write key outside SQL expiry witness no effects clock rollback denied");
            return;
        }
        var committed = await store.CommitAutomaticAsync(plan, dependencies, effectOperation, token);
        if (committed.WasAlreadyCommitted || committed.Scope != scope || committed.BatchId != allocated.Id || committed.RequestIds.Count != plan.NoteCount
            || await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, effectOperation, token) != expectedRows) throw new InvalidOperationException();
        var graph = await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, effectOperation, token);
        var rawGraph = await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, effectOperation, 2, token);
        var manifestGraph = await GroupAutomaticManifestRuntimeProof.RequireAsync(db, scope, effectOperation, token);
        await RequireDependenciesAsync();
        var readback = await brain.ReadAsync(handle, committed.RequestIds, [], token);
        if (readback.Items.Count != plan.NoteCount) throw new InvalidOperationException();
        for (var index = 0; index < committed.RequestIds.Count; index++)
        {
            var item = readback.Items.Single(x => x.RecordId == committed.RequestIds[index]);
            if (item.Revision != 1 || item.IsItConfirmed || item.Content.Contains("OWNED_AUTOMATIC_PRIVATE_SENTINEL", StringComparison.Ordinal))
                throw new InvalidOperationException();
            if (index < plan.AiNotes.Count)
            {
                if (proposal is null || source is null) throw new InvalidOperationException();
                var clear = GroupBrainPayloadCodec.DecodeAiNote(item.Content);
                if (item.Origin != GroupRequestRevisionOrigin.AiExtracted || item.VerificationLevel != GroupRequestVerificationLevel.SourceBackedAiInterpretation
                    || item.Content != GroupBrainPayloadCodec.EncodeAiNote(proposal.Notes[index]) || clear.Evidence.Count != 1
                    || clear.Evidence[0].MessageId != source.MessageId || clear.Evidence[0].Revision != source.Revision || clear.Evidence[0].Quote != source.Text)
                    throw new InvalidOperationException();
            }
            else
            {
                var host = plan.HostNotes[index - plan.AiNotes.Count]; var clear = GroupBrainPayloadCodec.DecodeHostAttention(item.Content);
                if (item.Origin != GroupRequestRevisionOrigin.HostAttention || item.VerificationLevel != GroupRequestVerificationLevel.HostObserved
                    || item.BusinessStatus != GroupNoteBusinessStatus.NeedsClarification || item.Content != host.EncodePayload()
                    || clear.Reason != host.Reason || clear.HasCoverageGap || !clear.SourceReferences.SequenceEqual(host.SourceReferences))
                    throw new InvalidOperationException();
            }
        }
        await RequireOriginalAsync();
        var replay = await store.CommitAutomaticAsync(plan, dependencies, effectOperation, token);
        if (!replay.WasAlreadyCommitted || replay.Scope != committed.Scope || replay.BatchId != committed.BatchId || replay.OperationId != committed.OperationId
            || replay.OutboxId != committed.OutboxId || replay.CommittedAtUtc != committed.CommittedAtUtc || !replay.RequestIds.SequenceEqual(committed.RequestIds))
            throw new InvalidOperationException();
        await RequireOriginalAsync();
        if (!hostOnly) await RefusedAsync(() => store.CommitAutomaticAsync(GroupAutomaticNotePlan.Create(preparation, Proposal(true)), dependencies, effectOperation, token));
        await RequireDependenciesAsync();
        await RequireOriginalAsync();
        var reads = keys.Reads; var writes = keys.Writes;
        await RefusedAsync(() => store.CommitAutomaticAsync(plan, dependencies, Guid.NewGuid(), token));
        await RequireOriginalAsync();
        if (reads != (hostOnly ? 3 : 4) || writes != 1 || keys.Reads != reads || keys.Writes != writes
            || await db.GroupCustomerRequests.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && (x.AssignedToUserId != null || x.CommittedDueAtUtc != null
                    || x.ConfirmedByUserId != null || x.ConfirmedAtUtc != null), token)) throw new InvalidOperationException();
        await GroupAutomaticRawRuntimeProof.RequireImmutableAsync(db, scope, effectOperation, token);
        await GroupAutomaticManifestRuntimeProof.RequireImmutableAsync(db, scope, effectOperation, token);
        await RequireOriginalAsync();
        await RequireDependenciesAsync();
        if (dependencyProof.Completed != 3) throw new InvalidOperationException();
        Console.WriteLine(hostOnly
            ? "PASS owned automatic host actual protected UnsupportedMedia note two metadata evidence two Attention dispositions atomic NotesCommitted original replay new nonce refusal no AI or IT authority"
            : "PASS owned automatic note protected mixed four notes five evidence two dispositions atomic NotesCommitted exact original replay changed plan new nonce denied no IT authority");
        Console.WriteLine(hostOnly
            ? "PASS owned automatic host whole dependency SQL three current reconstruction checks original graphs claim keys unchanged serializable source lock"
            : "PASS owned automatic note whole dependency SQL three current reconstruction checks original graphs claim keys unchanged serializable source lock");

        Task RequireDependenciesAsync() => GroupAutomaticDependencyRuntimeProof.RequireAsync(db, handle, worker, clock, sources, brain,
            keys, dependencyProof, RequireOriginalAsync, token);

        async Task RequireOriginalAsync()
        {
            if (await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, effectOperation, token) != expectedRows
                || await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, effectOperation, token) != graph
                || await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, effectOperation, 2, token) != rawGraph
                || await GroupAutomaticManifestRuntimeProof.RequireAsync(db, scope, effectOperation, token) != manifestGraph
                || db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null) throw new InvalidOperationException();
            GroupAutomaticRawRuntimeProof.RequireDetached(db);
        }
        async Task RequireEmptyEffectsAsync()
        {
            await GroupAutomaticRawRuntimeProof.RequireEmptyAsync(db, scope, token);
            GroupAutomaticRawRuntimeProof.RequireDetached(db);
            if (await db.GroupWorkCommitReceipts.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupWorkSourceDispositions.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupCustomerRequests.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupRequestRevisions.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupRequestEvidence.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupNotesCommittedOutbox.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupNotesCommittedItems.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null
                || db.ChangeTracker.Entries().Any(x => x.Entity is GroupWorkCommitReceiptRecord or GroupWorkSourceDispositionRecord
                    or GroupCustomerRequestRecord or GroupRequestRevisionRecord or GroupRequestEvidenceRecord or GroupNotesCommittedOutboxRecord or GroupNotesCommittedItemRecord))
                throw new InvalidOperationException();
        }
        GroupGroundedWorkProposal Proposal(bool changed)
        {
            if (source is null) throw new InvalidOperationException();
            return GroupGroundedWorkProposal.Parse(preparation, JsonSerializer.Serialize(new
            {
                version = GroupGroundedWorkProposal.FormatVersion,
                notes = new[] { Note("request", changed ? "owned changed interpretation" : "owned synthetic inventory question", []),
                    Note("needs_clarification", "owned synthetic missing warehouse", ["owned warehouse detail"]) },
                source_dispositions = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, disposition = "work" } }
            }));
        }
        object Note(string type, string title, string[] missing) => new
        {
            type,
            title,
            problem = "owned synthetic interpretation",
            outcome = "owned synthetic requested outcome",
            source_refs = new[] { new { message_id = (source ?? throw new InvalidOperationException()).MessageId.ToString("D"), revision = source.Revision, quote = source.Text } },
            missing_fields = missing,
            requested_deadline_text = (string?)null,
            suggested_relation = (string?)null
        };
    }
    private static async Task DeniedAsync(Func<Task<GroupNoteCommitResult>> action)
    {
        var denied = false; try { await action(); } catch (UnauthorizedAccessException) { denied = true; }
        if (!denied) throw new InvalidOperationException();
    }
    private static async Task RefusedAsync(Func<Task<GroupNoteCommitResult>> action)
    {
        var refused = false;
        try { await action(); } catch (InvalidOperationException error) when (error.Message == "Group note commit is unavailable." && error.InnerException is null) { refused = true; }
        if (!refused) throw new InvalidOperationException();
    }
}
