using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Owned disposable SQL only. Real ingress/allocation/note/selection/original
// stores, with fixed synthetic interpretations and no provider invocation.
internal static class GroupAutomaticHistoryRuntimeProof
{
    internal const string PrepareLine = "PASS owned history selection shipping four protected notes same current batch excluded actual serializable scoped metadata queries no provider or model";
    internal const string CommitLine = "PASS owned history selection actual next Core source code reply recent four prior notes bounded scoped SQL original nonempty history plan encrypted accepted result no provider or model";
    internal const string ResumeLine = "PASS owned history selection separate process replacement claim persisted four original history identities and order current source brain protected accepted result byte exact no new selection start or provider";
    internal static string Phase { get; private set; } = "setup";
    private const string Profile = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Model = "owned-synthetic-history";

    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("history-selection-prepare" or "history-selection-commit" or "history-selection-resume"))
            throw new InvalidOperationException();
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var probe = new SelectionProbe(scope);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options).AddInterceptors(probe).Options);
        Phase = "allocation";
        var outbox = await db.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.CommittedSequence).ToArrayAsync(token);
        var preparing = mode == "history-selection-prepare";
        if (outbox.Length != (preparing ? 2 : 3) || outbox[preparing ? 0 : 2].Id != operation
            || !outbox.Select(x => x.CommittedSequence).SequenceEqual(preparing ? [1L, 2L] : [1L, 2L, 3L]))
            throw new InvalidOperationException();
        if (mode != "history-selection-resume")
        {
            var previous = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderByDescending(x => x.ExpiresAtUtc).FirstOrDefaultAsync(token);
            var pendingAt = await db.GroupSourceStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.LastPendingAtUtc).SingleAsync(token)
                ?? throw new InvalidOperationException();
            clock.Current = pendingAt.AddMinutes(3);
            if (previous is not null && clock.Current <= previous.ExpiresAtUtc) clock.Current = previous.ExpiresAtUtc.AddTicks(1);
            foreach (var row in outbox.Skip(preparing ? 0 : 2))
                await new GroupIngressInboxStore(db, worker, clock).ReceiveAsync(new(1, scope, row.Id, row.MessageId, row.Revision, row.CommittedSequence), token);
            var allocated = await new GroupBatchAllocationStore(db, worker, new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)), clock)
                .AllocateDueAsync(scope, operation, token) ?? throw new InvalidOperationException();
            if (allocated.WasAlreadyAllocated || allocated.AfterSequence != (preparing ? 0 : 2)
                || allocated.AllocatedThroughSequence != (preparing ? 2 : 3) || allocated.Revisions.Count != (preparing ? 2 : 1))
                throw new InvalidOperationException();
        }
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var oldClaim = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id).OrderByDescending(x => x.Epoch).FirstOrDefaultAsync(token);
        if (mode == "history-selection-resume")
        {
            if (oldClaim is null || oldClaim.Epoch != 1) throw new InvalidOperationException();
            clock.Current = oldClaim.ExpiresAtUtc.AddTicks(1);
        }
        else if (oldClaim is not null) throw new InvalidOperationException();
        var claim = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, allocation.Id,
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        var handle = claim.CurrentHandle ?? throw new InvalidOperationException();
        if (claim.WasAlreadyClaimed || claim.Receipt.Epoch != (mode == "history-selection-resume" ? 2 : 1)) throw new InvalidOperationException();
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var ids = preparing ? outbox.Select(x => x.MessageId).ToArray() : [outbox[2].MessageId];
        Phase = "source";
        var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, ids, token));
        if (preparation.Candidates.Count != 1 || preparation.Receipts.Count != ids.Length || keys.Reads != 1 || keys.Writes != 0)
            throw new InvalidOperationException();
        var source = preparation.Candidates.Single();
        if (preparing)
        {
            if (source.Text != "Tra cứu tồn kho 😀\uFEFF " || preparation.Receipts.Count(x => x.Disposition == GroupSourcePreparationDisposition.Quarantined) != 1)
                throw new InvalidOperationException();
            Phase = "historical-notes";
            var history = await brain.ReadAsync(handle, [], [], token);
            var proposal = GroupGroundedWorkProposal.Parse(preparation, Output(preparation, true));
            var notePlan = GroupAutomaticNotePlan.Create(preparation, proposal);
            var effect = await new GroupNoteCommitStore(db, worker, clock, sources, brain, keys, new())
                .CommitAutomaticAsync(notePlan, history, Guid.NewGuid(), token);
            if (effect.WasAlreadyCommitted || effect.RequestIds.Count != 4 || notePlan.AiNotes.Count != 2 || notePlan.HostNotes.Count != 2
                || await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, effect.OperationId, token) != 21) throw new InvalidOperationException();
            var reads = keys.Reads; var writes = keys.Writes;
            Phase = "current-batch-exclusion";
            probe.Armed = true;
            GroupAutomaticHistorySelection selected;
            try { selected = await new GroupAutomaticHistorySelector(db, worker, clock, sources).SelectAsync(preparation, token); }
            finally { probe.Armed = false; }
            if (selected.RequestIds.Count != 0 || selected.GlossaryIds.Count != 0 || selected.HasAdditionalHistory
                || keys.Reads != reads || keys.Writes != writes || probe.Queries != 2) throw new InvalidOperationException();
            RequireClean(db); Console.WriteLine(PrepareLine); return;
        }
        Phase = "history-original";
        var planStore = new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain);
        GroupAutomaticInputPlan plan;
        GroupBrainPrivateContext dependencies;
        if (mode == "history-selection-commit")
        {
            if (await planStore.ReadAsync(handle, token) is not null) throw new InvalidOperationException();
            var hints = GroupAutomaticHistoryHints.Create(preparation, token);
            if (hints.RequestCodes.Count != 1 || hints.ReplyMessageIndexes.Count != 1) throw new InvalidOperationException();
            var reads = keys.Reads; var writes = keys.Writes;
            probe.Armed = true;
            GroupAutomaticHistorySelection selected;
            try { selected = await new GroupAutomaticHistorySelector(db, worker, clock, sources).SelectAsync(preparation, token); }
            finally { probe.Armed = false; }
            if (selected.RequestIds.Count != 4 || selected.GlossaryIds.Count != 0 || selected.HasAdditionalHistory || probe.Queries != 4
                || keys.Reads != reads || keys.Writes != writes) throw new InvalidOperationException();
            var exactCode = await db.GroupCustomerRequests.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.RequestCode == hints.RequestCodes[0], token);
            if (selected.RequestIds[0] != exactCode.Id || exactCode.OriginBatchId == allocation.Id) throw new InvalidOperationException();
            dependencies = await brain.ReadAsync(handle, selected.RequestIds, [], token);
            RequireHistory(dependencies);
            plan = GroupAutomaticInputPlan.Create(preparation, dependencies, Profile, Model, 64000, await OriginsAsync(), token);
            plan = await planStore.PersistAsync(plan, preparation, dependencies, token);
            if (!plan.HistoryOrder.Select(x => x.RecordId).SequenceEqual(dependencies.Items.Select(x => x.RecordId))) throw new InvalidOperationException();
        }
        else
        {
            // Recovery must read persisted identities before any selection.
            plan = await planStore.ReadAsync(handle, token) ?? throw new InvalidOperationException();
            if (plan.HistoryOrder.Count != 4 || plan.HistoryOrder.Any(x => x.Kind != GroupBrainContentKind.RequestRevision)) throw new InvalidOperationException();
            dependencies = await brain.ReadAsync(handle, plan.HistoryOrder.Select(x => x.RecordId).ToArray(), [], token);
            RequireHistory(dependencies);
            if (probe.Queries != 0 || keys.Writes != 0) throw new InvalidOperationException();
        }
        var chunk = plan.RestoreChunks(preparation, dependencies, Profile, await OriginsAsync(), token).Single();
        var journal = new GroupModelAttemptJournalStore(db, worker, clock, sources, brain);
        var results = new GroupModelAttemptResultStore(db, worker, clock, sources, brain, keys, new());
        Phase = "accepted-result";
        GroupModelAttemptResult accepted;
        if (mode == "history-selection-commit")
        {
            if (await results.ReadOriginalAsync(plan, chunk, token) is not null) throw new InvalidOperationException();
            var start = await journal.TryStartOriginalAsync(plan, chunk, Guid.NewGuid(), token);
            var permit = start.Permit ?? throw new InvalidOperationException();
            var request = permit.Context.CreateGatewayRequest(permit.Start.RequestId);
            using var input = JsonDocument.Parse(request.Input);
            if (input.RootElement.GetProperty("same_group_history").GetArrayLength() != 4) throw new InvalidOperationException();
            var response = GroupModelAttemptResponse.Parse(permit, new AiGatewayResponse(request.RequestId, request.Capability,
                Output(preparation, false), Model, 123, 7)
            { ProviderId = "owned-synthetic-history-provider" }, clock, token);
            accepted = await results.PersistOriginalAsync(plan, chunk, response, token);
        }
        else
        {
            accepted = await results.ReadOriginalAsync(plan, chunk, token) ?? throw new InvalidOperationException();
            var refused = false;
            try { await journal.TryStartOriginalAsync(plan, chunk, Guid.NewGuid(), token); }
            catch (InvalidOperationException)
            { refused = true; }
            if (!refused || keys.Writes != 0 || keys.Reads != 3 || probe.Queries != 0) throw new InvalidOperationException();
        }
        if (!accepted.Proposal.EncodeValidatedOutput().AsSpan().SequenceEqual(GroupGroundedWorkProposal.Parse(preparation, Output(preparation, false)).EncodeValidatedOutput())
            || accepted.Observed.ProviderId != "owned-synthetic-history-provider" || accepted.Observed.Model != Model
            || await db.GroupModelAttemptStarts.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token) != 1
            || await db.GroupModelAttemptFailures.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId, token)
            || await db.GroupWorkCommitReceipts.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token)) throw new InvalidOperationException();
        RequireClean(db); Console.WriteLine(mode == "history-selection-commit" ? CommitLine : ResumeLine);

        async Task<GroupBrainRequestOriginMetadata[]> OriginsAsync() => await db.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.Id)
            .Select(x => new GroupBrainRequestOriginMetadata(scope, x.Id, x.OriginBatchId, x.OriginOperationId, x.OriginCandidateOrdinal, x.CreatedAtUtc)).ToArrayAsync(token);
        void RequireHistory(GroupBrainPrivateContext history)
        {
            if (history.Items.Count != 4 || history.Items.Any(x => x.Kind != GroupBrainContentKind.RequestRevision || x.IsItConfirmed)
                || history.Items.Count(x => x.Origin == GroupRequestRevisionOrigin.AiExtracted) != 2
                || history.Items.Count(x => x.Origin == GroupRequestRevisionOrigin.HostAttention) != 2) throw new InvalidOperationException();
        }
    }

    private static string Output(GroupBatchSourcePreparation preparation, bool initial)
    {
        var source = preparation.Candidates.Single();
        return JsonSerializer.Serialize(new
        {
            version = GroupGroundedWorkProposal.FormatVersion,
            notes = initial ? new[] { Note("request", "owned historical inventory request", []), Note("needs_clarification", "owned historical warehouse question", ["warehouse"]) }
                : new[] { Note("needs_clarification", "owned followup inventory question", ["warehouse detail"]) },
            source_dispositions = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, disposition = "work" } }
        });
        object Note(string type, string title, string[] missing) => new
        {
            type,
            title,
            problem = "owned synthetic inventory context",
            outcome = "owned synthetic inventory lookup",
            source_refs = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, quote = source.Text } },
            missing_fields = missing,
            requested_deadline_text = (string?)null,
            suggested_relation = (string?)null
        };
    }
    private static void RequireClean(PlatformDbContext db)
    {
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed)
            throw new InvalidOperationException();
    }
    private sealed class SelectionProbe(GroupScope scope) : DbCommandInterceptor
    {
        internal bool Armed;
        internal int Queries;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (!Armed || !command.CommandText.Contains("FROM [aioffice].[GroupCustomerRequests]", StringComparison.Ordinal)
                && !command.CommandText.Contains("FROM [aioffice].[GroupGlossaryEntries]", StringComparison.Ordinal)) return result;
            if (command.Transaction is not { IsolationLevel: IsolationLevel.Serializable } transaction || transaction.Connection is null
                || command.CommandText.Contains("ProtectedContent", StringComparison.Ordinal)
                || command.CommandText.Contains("GroupRequestRevisions", StringComparison.Ordinal)
                || !command.CommandText.Contains("TOP(", StringComparison.Ordinal)
                || !command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, scope.TenantId))
                || !command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, scope.CompanyId))
                || !command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, scope.SourceBindingId))) throw new InvalidOperationException();
            await using var check = transaction.Connection.CreateCommand(); check.Transaction = transaction; check.CommandTimeout = 5;
            check.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var resource = check.CreateParameter(); resource.ParameterName = "@resource";
            resource.Value = $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}"; check.Parameters.Add(resource);
            if (!string.Equals((string?)await check.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
            Queries++; return result;
        }
    }
}
