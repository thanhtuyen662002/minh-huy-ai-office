using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Owned disposable SQL only. Real ingress/allocation/note/selection/original
// stores, with fixed synthetic interpretations and no provider invocation.
internal static class GroupOriginalInputV3RuntimeProof
{
    internal const string PrepareLine = "PASS owned V3 original shipping four protected notes current batch excluded actual scoped selector issuance no provider or model";
    internal const string CommitLine = "PASS owned V3 original actual nonempty history issued selector SQL commit reply lost fresh locked read same immutable V3 bytes encrypted accepted result physical rights and format bounds no provider or model";
    internal const string ResumeLine = "PASS owned V3 original separate process replacement current epoch two original epoch one disclosure input hashes chunks operations history order encrypted result exact no reselection second start or provider";
    internal static string Phase { get; private set; } = "setup";
    internal const string MismatchLine = "PASS owned V3 original actual row2 wire3 refused under current locked read zero keys starts results or effects";
    private const string Profile = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Model = "owned-synthetic-history";

    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("v3-history-prepare" or "v3-history-commit" or "v3-history-resume" or "v3-history-mismatch"))
            throw new InvalidOperationException();
        if (mode == "v3-history-mismatch")
        {
            await RequireMismatchedReadAsync(scope, operation, worker, options, token); Console.WriteLine(MismatchLine); return;
        }
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var probe = new SelectionProbe(scope); var loss = new CommitReplyLossProbe();
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options).AddInterceptors(probe, loss).Options);
        Phase = "allocation";
        var outbox = await db.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.CommittedSequence).ToArrayAsync(token);
        var preparing = mode == "v3-history-prepare";
        if (outbox.Length != (preparing ? 2 : 3) || outbox[preparing ? 0 : 2].Id != operation
            || !outbox.Select(x => x.CommittedSequence).SequenceEqual(preparing ? [1L, 2L] : [1L, 2L, 3L]))
            throw new InvalidOperationException();
        if (mode != "v3-history-resume")
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
        if (mode == "v3-history-resume")
        {
            if (oldClaim is null || oldClaim.Epoch != 1) throw new InvalidOperationException();
            clock.Current = oldClaim.ExpiresAtUtc.AddTicks(1);
        }
        else if (oldClaim is not null) throw new InvalidOperationException();
        var claim = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, allocation.Id,
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        var handle = claim.CurrentHandle ?? throw new InvalidOperationException();
        if (claim.WasAlreadyClaimed || claim.Receipt.Epoch != (mode == "v3-history-resume" ? 2 : 1)) throw new InvalidOperationException();
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
        if (mode == "v3-history-commit")
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
            plan = GroupAutomaticInputPlan.CreateV3(preparation, dependencies, selected, Profile, Model, 64000, await OriginsAsync(), token);
            var originalBytes = plan.Encode();
            Phase = "plan-commit-lost-reply"; loss.Armed = true; var interrupted = false;
            try { _ = await planStore.PersistV3Async(plan, preparation, dependencies, token); }
            catch (OwnedV3ReplyLostException) { interrupted = true; }
            finally { loss.Armed = false; }
            if (!interrupted || !loss.Lost || db.ChangeTracker.HasChanges() || db.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any())
                throw new InvalidOperationException();
            await using (var fresh = new PlatformDbContext(options))
            {
                var freshKeys = new GroupNoteRuntimeProof.CountedKeys(fresh, scope);
                var freshSources = new GroupBatchSourceReader(fresh, worker, clock, freshKeys, new());
                var freshBrain = new GroupBrainCurrentReader(fresh, worker, clock, freshKeys, new());
                plan = await new GroupAutomaticInputPlanStore(fresh, worker, clock, freshSources, freshBrain).ReadAsync(handle, token)
                    ?? throw new InvalidOperationException();
                if (freshKeys.Reads != 0 || freshKeys.Writes != 0) throw new InvalidOperationException();
                RequireClean(fresh);
            }
            if (plan.WireFormatVersion != 3 || !originalBytes.AsSpan().SequenceEqual(plan.Encode())) throw new InvalidOperationException();
            var commandsBefore = probe.Commands; var structuralRefused = false;
            try { _ = await planStore.PersistV3Async(plan, preparation, dependencies, token); }
            catch (InvalidOperationException error) when (error.Message == "Group original input plan store is not available." && error.InnerException is null)
            { structuralRefused = true; }
            if (!structuralRefused || probe.Commands != commandsBefore) throw new InvalidOperationException();
            Phase = "physical-boundaries"; await RequirePhysicalBoundariesAsync(db, scope, allocation.Id, token);
            if (!plan.HistoryOrder.Select(x => x.RecordId).SequenceEqual(dependencies.Items.Select(x => x.RecordId))) throw new InvalidOperationException();
        }
        else
        {
            // Recovery must read persisted identities before any selection.
            plan = await planStore.ReadAsync(handle, token) ?? throw new InvalidOperationException();
            if (plan.WireFormatVersion != 3 || plan.HistoryOrder.Count != 4 || plan.HistoryOrder.Any(x => x.Kind != GroupBrainContentKind.RequestRevision)) throw new InvalidOperationException();
            dependencies = await brain.ReadAsync(handle, plan.HistoryOrder.Select(x => x.RecordId).ToArray(), [], token);
            RequireHistory(dependencies);
            if (probe.Queries != 0 || keys.Writes != 0) throw new InvalidOperationException();
        }
        var chunk = plan.RestoreChunks(preparation, dependencies, Profile, await OriginsAsync(), token).Single();
        var journal = new GroupModelAttemptJournalStore(db, worker, clock, sources, brain);
        var results = new GroupModelAttemptResultStore(db, worker, clock, sources, brain, keys, new());
        Phase = "accepted-result";
        GroupModelAttemptResult accepted;
        if (mode == "v3-history-commit")
        {
            if (await results.ReadOriginalAsync(plan, chunk, token) is not null) throw new InvalidOperationException();
            var start = await journal.TryStartOriginalAsync(plan, chunk, Guid.NewGuid(), token);
            var permit = start.Permit ?? throw new InvalidOperationException();
            var request = permit.Context.CreateGatewayRequest(permit.Start.RequestId);
            using var input = JsonDocument.Parse(request.Input);
            if (input.RootElement.GetProperty("same_group_history").GetArrayLength() != 4
                || input.RootElement.GetProperty("history_selection").GetProperty("completeness").GetString() != "not_asserted"
                || input.RootElement.GetProperty("history_selection").GetProperty("selection_may_be_incomplete").GetBoolean()) throw new InvalidOperationException();
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
        RequireClean(db); Console.WriteLine(mode == "v3-history-commit" ? CommitLine : ResumeLine);

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

    private sealed class OwnedV3ReplyLostException : IOException;
    private sealed class CommitReplyLossProbe : DbTransactionInterceptor
    {
        internal bool Armed, Lost;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            if (Armed && data.Context is PlatformDbContext db && db.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any())
            { Armed = false; Lost = true; throw new OwnedV3ReplyLostException(); }
            return Task.CompletedTask;
        }
    }

    private static async Task RequireMismatchedReadAsync(GroupScope scope, Guid operation, GroupExtractionWorkerBinding worker,
        DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        Phase = "mismatch-current-claim";
        await using var db = new PlatformDbContext(options);
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var original = await db.GroupBatchClaimReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id && x.Epoch == 1, token);
        var clock = new GroupNoteRuntimeProof.OwnedClock(original.IssuedAtUtc);
        var claimed = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, allocation.Id,
            original.OwnerId, original.OperationId, TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        var handle = claimed.CurrentHandle ?? throw new InvalidOperationException();
        if (!claimed.WasAlreadyClaimed || claimed.Receipt.BatchId != original.BatchId || claimed.Receipt.OperationId != original.OperationId
            || claimed.Receipt.OwnerId != original.OwnerId || claimed.Receipt.Epoch != original.Epoch) throw new InvalidOperationException();
        Phase = "mismatch-row";
        var row = await db.Database.SqlQuery<MismatchRow>($"""
            SELECT TenantId,CompanyId,BindingId,BatchId,FormatVersion,
              CASE WHEN DATALENGTH([Plan]) BETWEEN 256 AND 32000 AND SUBSTRING([Plan],1,8)=0x41494F47504C4E33
                AND PlanSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',[Plan]),2) THEN 1 ELSE 0 END AS CompleteV3
            FROM aioffice.GroupAutomaticInputPlans
            """).SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        if (row.FormatVersion != 2 || row.CompleteV3 != 1) throw new InvalidOperationException();
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        Phase = "mismatch-read-refusal"; var refused = false;
        try { _ = await new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain).ReadAsync(handle, token); }
        catch (InvalidOperationException error) when (error.Message == "Group original input plan store is not available." && error.InnerException is null)
        { refused = true; }
        if (!refused || keys.Reads != 0 || keys.Writes != 0) throw new InvalidOperationException();
        RequireClean(db);
    }
    private sealed record MismatchRow(Guid TenantId, Guid CompanyId, Guid BindingId, Guid BatchId, int FormatVersion, int CompleteV3);

    private static async Task RequirePhysicalBoundariesAsync(PlatformDbContext db, GroupScope scope, Guid original, CancellationToken token)
    {
        const string allocate = "INSERT INTO aioffice.GroupBatchAllocations(TenantId,CompanyId,BindingId,Id,OperationId,AfterSequence,AllocatedThroughSequence,ObservedCommittedThroughSequence,RawRevisionCount,IsHistoricalBackfill,SourceVersion,DeletionGeneration,AccountVersion,ServiceId,CredentialEpoch,GrantVersion,AllocatedAtUtc) SELECT TenantId,CompanyId,BindingId,@batch,@operation,AllocatedThroughSequence,AllocatedThroughSequence+RawRevisionCount,AllocatedThroughSequence+RawRevisionCount,RawRevisionCount,IsHistoricalBackfill,SourceVersion,DeletionGeneration,AccountVersion,ServiceId,CredentialEpoch,GrantVersion,AllocatedAtUtc FROM aioffice.GroupBatchAllocations WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND Id=@original;";
        const string claim = "INSERT INTO aioffice.GroupBatchClaimReceipts(TenantId,CompanyId,BindingId,OperationId,BatchId,OwnerId,Epoch,RequestedLifetimeTicks,IssuedAtUtc,ExpiresAtUtc,ServiceId,CredentialEpoch,GrantVersion,SourceVersion,DeletionGeneration,AccountVersion,AuthoritySha256) SELECT TenantId,CompanyId,BindingId,@operation,@batch,OwnerId,Epoch,RequestedLifetimeTicks,IssuedAtUtc,ExpiresAtUtc,ServiceId,CredentialEpoch,GrantVersion,SourceVersion,DeletionGeneration,AccountVersion,AuthoritySha256 FROM aioffice.GroupBatchClaimReceipts WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND BatchId=@original AND Epoch=1;";
        const string insert = "INSERT INTO aioffice.GroupAutomaticInputPlans SELECT TenantId,CompanyId,BindingId,@batch,{0},PlanSha256,{1},@operation,OriginalClaimEpoch,CreatedAtUtc FROM aioffice.GroupAutomaticInputPlans WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND BatchId=@original;";
        var probes = new (string Sql, int Error, string Boundary)[]
        {
            (allocate + claim + string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, "3", "[Plan]"), 0, ""),
            ("UPDATE aioffice.GroupAutomaticInputPlans SET FormatVersion=FormatVersion WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND BatchId=@original;", 229, "GroupAutomaticInputPlans"),
            ("UPDATE aioffice.GroupAutomaticInputPlans SET PlanSha256=PlanSha256 WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND BatchId=@original;", 229, "GroupAutomaticInputPlans"),
            ("DELETE FROM aioffice.GroupAutomaticInputPlans WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND BatchId=@original;", 229, "GroupAutomaticInputPlans"),
            (allocate + claim + string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, "4", "[Plan]"), 547, "CK_GroupAutomaticInputPlans_Format"),
            (allocate + claim + string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, "3", "CONVERT(varbinary(max),REPLICATE(CAST('x' AS varchar(max)),32001))"), 547, "CK_GroupAutomaticInputPlans_Format")
        };
        foreach (var probe in probes)
        {
            await using var connection = new SqlConnection(db.Database.GetConnectionString()); await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await using var command = new SqlCommand(probe.Sql, connection, transaction) { CommandTimeout = 10 };
            command.Parameters.AddWithValue("@tenant", scope.TenantId); command.Parameters.AddWithValue("@company", scope.CompanyId);
            command.Parameters.AddWithValue("@binding", scope.SourceBindingId); command.Parameters.AddWithValue("@original", original);
            command.Parameters.AddWithValue("@batch", Guid.NewGuid()); command.Parameters.AddWithValue("@operation", Guid.NewGuid());
            var matched = false;
            try { var affected = await command.ExecuteNonQueryAsync(token); matched = probe.Error == 0 && affected == 3; }
            catch (SqlException error) when (probe.Error != 0 && error.Number == probe.Error && error.Message.Contains(probe.Boundary, StringComparison.Ordinal))
            { matched = true; }
            await transaction.RollbackAsync(token);
            if (!matched) throw new InvalidOperationException();
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
    internal sealed class SelectionProbe(GroupScope scope) : DbCommandInterceptor
    {
        internal bool Armed;
        internal int Queries;
        internal int Commands;
        // Observe the selector's actual closed Candidate projection even when
        // validation is disarmed. Exact-ID brain heads and origin/fence reads
        // have different projections, including normal Single's TOP(2).
        internal bool ObserveSelection(string sql)
        {
            if (!Regex.IsMatch(sql,
                @"SELECT (?:TOP\(@[A-Za-z0-9_]+\) )?\[(?<alias>[A-Za-z0-9_]+)\]\.\[Id\], \[\k<alias>\]\.\[CurrentRevision\], \[\k<alias>\]\.\[(?:UpdatedAtUtc|CreatedAtUtc)\]\r?\nFROM \[aioffice\]\.\[(?:GroupCustomerRequests|GroupGlossaryEntries)\] AS \[\k<alias>\]",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
            Queries++;
            if (!Armed) throw new InvalidOperationException("Owned V3 recovery attempted history reselection.");
            return true;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        { Commands++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<object> result, CancellationToken token = default)
        { Commands++; return ValueTask.FromResult(result); }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            Commands++;
            if (!ObserveSelection(command.CommandText)) return result;
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
            return result;
        }
    }
}
