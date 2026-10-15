extern alias RuntimeWorker;

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Owned disposable SQL only. Uses the shipping Gateway admission/tokenizer
// with an explicitly synthetic catalog and an HTTP trap. No model evaluation.
internal static class GroupOriginalAdmissionRuntimeProof
{
    internal const string PrepareLine = "PASS owned original admission actual two revisions inbox allocation no original sidecar attempt result or effects";
    internal const string ExpiryLine = "PASS owned original admission actual both rows flushed expiry observation retained across immediate rewind rollback witness committed old claim refused legitimate replacement retains zero effects or HTTP";
    internal const string CommitLine = "PASS owned original admission actual shipping gate tokenizer own carrier atomic two row rollback committed reply loss fresh context exact immutable replay every column deny encrypted accepted result zero HTTP";
    internal const string RecoverLine = "PASS owned original admission separate process epoch three accepted result FIRST recovered with sidecar SELECT unavailable empty released catalog zero admission calls or HTTP";
    internal const string ResumeLine = "PASS owned original admission current epoch three exact original epoch two plan sidecar snapshots legacy input identities chunks hashes restored zero provider";
    private const string Profile = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Model = "owned-synthetic-admission";
    internal static string Phase { get; private set; } = "setup";

    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("admission-original-prepare" or "admission-original-expiry" or "admission-original-commit" or "admission-original-recover" or "admission-original-resume"))
            throw new InvalidOperationException();
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var probe = new AtomicProbe(scope, clock);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(probe, new FlushProbe(probe), new CommitProbe(probe)).Options);
        Phase = "allocation";
        if (mode == "admission-original-prepare")
        {
            var pending = await db.GroupSourceStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
            clock.Current = (pending.LastPendingAtUtc ?? throw new InvalidOperationException()).AddMinutes(3);
            var outbox = await db.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.CommittedSequence).ToArrayAsync(token);
            if (outbox.Length != 2 || outbox[0].Id != operation || !outbox.Select(x => x.CommittedSequence).SequenceEqual([1L, 2L]))
                throw new InvalidOperationException();
            foreach (var row in outbox)
                await new GroupIngressInboxStore(db, worker, clock).ReceiveAsync(new(1, scope, row.Id, row.MessageId, row.Revision, row.CommittedSequence), token);
            var allocated = await new GroupBatchAllocationStore(db, worker, new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)), clock)
                .AllocateDueAsync(scope, operation, token) ?? throw new InvalidOperationException();
            if (allocated.WasAlreadyAllocated || allocated.AfterSequence != 0 || allocated.AllocatedThroughSequence != 2 || allocated.Revisions.Count != 2
                || await db.GroupBatchClaimReceipts.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token))
                throw new InvalidOperationException();
            await EmptyEffectsAsync(); RequireClean(db); Console.WriteLine(PrepareLine); return;
        }
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var old = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id)
            .OrderByDescending(x => x.Epoch).FirstOrDefaultAsync(token);
        var expectedEpoch = mode == "admission-original-expiry" ? 1 : mode == "admission-original-commit" ? 2 : 3;
        if ((old?.Epoch ?? 0) != (mode == "admission-original-resume" ? expectedEpoch : expectedEpoch - 1)) throw new InvalidOperationException();
        clock.Current = old is null ? allocation.AllocatedAtUtc : mode == "admission-original-resume" ? old.IssuedAtUtc : old.ExpiresAtUtc.AddTicks(1);
        var claims = new GroupBatchClaimStore(db, worker, clock);
        var claim = await claims.TryAcquireAsync(scope, allocation.Id,
            mode == "admission-original-resume" ? old!.OwnerId : Guid.NewGuid(),
            mode == "admission-original-resume" ? old!.OperationId : Guid.NewGuid(), TimeSpan.FromMinutes(2), token)
            ?? throw new InvalidOperationException();
        var handle = claim.CurrentHandle ?? throw new InvalidOperationException();
        if (claim.Receipt.Epoch != expectedEpoch || claim.WasAlreadyClaimed != (mode == "admission-original-resume")) throw new InvalidOperationException();
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        Phase = "private-input";
        var members = await claims.ReadCurrentAllocationAsync(handle, token);
        var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, members.Revisions.Select(x => x.Metadata.MessageId).ToArray(), token));
        var dependencies = await brain.ReadAsync(handle, [], [], token);
        if (preparation.Candidates.Count != 1 || preparation.Receipts.Count != 2 || dependencies.Items.Count != 0 || keys.Reads != 1 || keys.Writes != 0)
            throw new InvalidOperationException();
        var legacyStore = new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain);
        GroupAutomaticInputPlan plan;
        using var http = new HttpClient(new NoHttp());
        var gateway = new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(http,
            new(new Uri("https://owned.invalid/responses"), Model, "Bearer owned-synthetic", "owned-synthetic", 2048)));
        Phase = "original";
        if (mode is "admission-original-expiry" or "admission-original-commit")
        {
            var selected = await new GroupAutomaticHistorySelector(db, worker, clock, sources).SelectAsync(preparation, token);
            if (selected.RequestIds.Count != 0 || selected.GlossaryIds.Count != 0 || selected.HasAdditionalHistory) throw new InvalidOperationException();
            plan = GroupAutomaticInputPlan.CreateV3(preparation, dependencies, selected, Profile, Model, 64000, [], token);
            var request = plan.RestoreChunks(preparation, dependencies, Profile, [], token).Single().Current.ModelContext!
                .CreateGatewayRequest(plan.Chunks.Single().OperationId);
            var metadata = gateway.DescribeStructuredGateway(); var route = metadata.Routes.Single();
            var candidate = new StructuredModelProfileCandidate(new AiModelProfile(route.ProviderId, Model,
                new HashSet<AiCapability> { AiCapability.StructuredGeneration }, 1, 0, 1), metadata.MetadataSha256, 0,
                route.EndpointIdentitySha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ResponseSchema!))),
                StructuredTextTokenCounter.Version, 128000, 1024, 2048, [Model], Profile);
            var gate = new StructuredModelAdmission(gateway, StructuredModelProfileCatalog.ForSyntheticControls(candidate), new BoundedStructuredTextTokenCounter(), clock);
            var carrierClock = new CarrierClock(clock);
            probe.CarrierClock = carrierClock;
            var preparer = new GroupOriginalAdmissionPreparer(gate, carrierClock);
            var issued = await preparer.PrepareAsync(plan, preparation, dependencies, [], token);
            var store = new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain, preparer);
            var originalBytes = plan.Encode(); var sidecarBytes = issued.Sidecar.Encode();
            if (mode == "admission-original-expiry")
            {
                Phase = "expiry-after-both-flushed"; probe.ExpiresAtUtc = claim.Receipt.ExpiresAtUtc; probe.ExpireAfterFlush = true;
                try { await RefusedAsync(() => store.PersistV3WithAdmissionAsync(issued, preparation, dependencies, token)); }
                finally { probe.ExpireAfterFlush = false; }
                if (!probe.SawBothFlushed || !carrierClock.ObservedAndRewound || await PlanCountAsync() != 0 || await AdmissionCountAsync() != 0) throw new InvalidOperationException();
                var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
                if (state.ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc) throw new InvalidOperationException();
                RequireClean(db); await EmptyEffectsAsync();
                Phase = "expiry-rewind-refusal"; clock.Current = claim.Receipt.IssuedAtUtc;
                await RefusedAsync(() => legacyStore.ReadAsync(handle, token));
                await RefusedAsync(() => store.PersistV3WithAdmissionAsync(issued, preparation, dependencies, token));
                if (await PlanCountAsync() != 0 || await AdmissionCountAsync() != 0
                    || await db.GroupBatchClaimStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                        && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id)
                        .Select(x => x.ExpiryObservedAtUtc).SingleAsync(token) != claim.Receipt.ExpiresAtUtc) throw new InvalidOperationException();
                RequireClean(db); await EmptyEffectsAsync(); Console.WriteLine(ExpiryLine); return;
            }
            Phase = "atomic-rollback"; probe.RollbackAfterFlush = true;
            var rolledBack = false;
            try { await store.PersistV3WithAdmissionAsync(issued, preparation, dependencies, token); }
            catch (OwnedRollback) { rolledBack = true; }
            finally { probe.RollbackAfterFlush = false; }
            if (!rolledBack || !probe.SawBothFlushed || await PlanCountAsync() != 0 || await AdmissionCountAsync() != 0) throw new InvalidOperationException();
            RequireClean(db); await EmptyEffectsAsync();
            Phase = "committed-reply-loss"; probe.LoseReply = true; var lost = false;
            try { await store.PersistV3WithAdmissionAsync(issued, preparation, dependencies, token); }
            catch (OwnedReplyLost) { lost = true; }
            finally { probe.LoseReply = false; }
            if (!lost || !probe.ReplyLost || await PlanCountAsync() != 1 || await AdmissionCountAsync() != 1) throw new InvalidOperationException();
            RequireClean(db);
            Phase = "fresh-read-replay";
            await using (var fresh = new PlatformDbContext(options))
            {
                var freshKeys = new GroupNoteRuntimeProof.CountedKeys(fresh, scope);
                var fs = new GroupBatchSourceReader(fresh, worker, clock, freshKeys, new());
                var fb = new GroupBrainCurrentReader(fresh, worker, clock, freshKeys, new());
                var freshStore = new GroupAutomaticInputPlanStore(fresh, worker, clock, fs, fb);
                var loaded = await freshStore.ReadAsync(handle, token) ?? throw new InvalidOperationException();
                var admission = await freshStore.ReadOriginalAdmissionAsync(handle, loaded, token) ?? throw new InvalidOperationException();
                Same(originalBytes, loaded.Encode()); Same(sidecarBytes, admission.Encode());
                if (freshKeys.Reads != 0 || freshKeys.Writes != 0) throw new InvalidOperationException(); RequireClean(fresh);
            }
            Same(originalBytes, (await store.PersistV3WithAdmissionAsync(issued, preparation, dependencies, token)).Encode());
            Same(sidecarBytes, (await store.ReadOriginalAdmissionAsync(handle, plan, token) ?? throw new InvalidOperationException()).Encode());
            Phase = "physical-column-boundaries";
            await RequireMutationRefusedAsync(db, scope, token);
            Phase = "accepted-result";
            var chunk = plan.RestoreChunks(preparation, dependencies, Profile, [], token).Single();
            var start = await new GroupModelAttemptJournalStore(db, worker, clock, sources, brain).TryStartOriginalAsync(plan, chunk, Guid.NewGuid(), token);
            var permit = start.Permit ?? throw new InvalidOperationException();
            if (start.WasAlreadyStarted) throw new InvalidOperationException();
            var results = new GroupModelAttemptResultStore(db, worker, clock, sources, brain, keys, new());
            var acceptedRequest = permit.Context.CreateGatewayRequest(permit.Start.RequestId);
            var response = GroupModelAttemptResponse.Parse(permit, new AiGatewayResponse(acceptedRequest.RequestId, acceptedRequest.Capability,
                Output(preparation), Model, 123, 7)
            { ProviderId = "owned-synthetic" }, clock, token);
            var accepted = await results.PersistOriginalAsync(plan, chunk, response, token);
            Same(GroupGroundedWorkProposal.Parse(preparation, Output(preparation)).EncodeValidatedOutput(), accepted.Proposal.EncodeValidatedOutput());
            if (keys.Writes != 1) throw new InvalidOperationException();
        }
        else
        {
            // Deliberately no admission service or sidecar query before this
            // old accepted-result path, including with SQL SELECT revoked.
            probe.ForbidAdmissionReads = mode == "admission-original-recover";
            plan = await legacyStore.ReadAsync(handle, token) ?? throw new InvalidOperationException();
            var chunk = plan.RestoreChunks(preparation, dependencies, Profile, [], token).Single();
            var recovered = await new GroupModelAttemptResultStore(db, worker, clock, sources, brain, keys, new())
                .ReadOriginalAsync(plan, chunk, token) ?? throw new InvalidOperationException();
            Same(GroupGroundedWorkProposal.Parse(preparation, Output(preparation)).EncodeValidatedOutput(), recovered.Proposal.EncodeValidatedOutput());
            if (keys.Writes != 0 || keys.Reads != 2) throw new InvalidOperationException();
            if (mode == "admission-original-resume")
            {
                var admission = await legacyStore.ReadOriginalAdmissionAsync(handle, plan, token) ?? throw new InvalidOperationException();
                admission.RequirePlan(plan);
                if (admission.OriginalClaimEpoch != 2 || admission.Entries.Count != plan.Chunks.Count
                    || admission.Entries.Single().Snapshot is null || admission.OriginalClaimOperationId == claim.Receipt.OperationId) throw new InvalidOperationException();
            }
            else
            {
                // Production catalog is still EMPTY. No gate is called in
                // recovery; constructing the production path adds no release.
                _ = new StructuredModelAdmission(gateway);
            }
        }
        RequireClean(db);
        if (await PlanCountAsync() != 1 || mode != "admission-original-recover" && await AdmissionCountAsync() != 1) throw new InvalidOperationException();
        Console.WriteLine(mode switch { "admission-original-commit" => CommitLine, "admission-original-recover" => RecoverLine, _ => ResumeLine });

        Task<int> PlanCountAsync() => db.GroupAutomaticInputPlans.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        Task<int> AdmissionCountAsync() => db.GroupOriginalAdmissionSidecars.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        async Task EmptyEffectsAsync()
        {
            if (await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, Guid.Empty, token) != 0
                || await db.GroupModelAttemptStarts.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupModelAttemptResults.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
        }
    }

    private static async Task RefusedAsync(Func<Task> operation)
    {
        var refused = false;
        try { await operation(); }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException) { refused = true; }
        if (!refused) throw new InvalidOperationException();
    }
    private static void Same(byte[] expected, byte[] actual) { if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidOperationException(); }
    private static void RequireClean(PlatformDbContext db)
    {
        if (db.ChangeTracker.HasChanges() || db.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any()
            || db.ChangeTracker.Entries<GroupOriginalAdmissionSidecarRecord>().Any() || db.Database.CurrentTransaction is not null
            || db.Database.GetDbConnection().State != ConnectionState.Closed) throw new InvalidOperationException();
    }
    private static string Output(GroupBatchSourcePreparation preparation)
    {
        var source = preparation.Candidates.Single();
        return JsonSerializer.Serialize(new
        {
            version = GroupGroundedWorkProposal.FormatVersion,
            notes = new[] { new { type = "needs_clarification", title = "owned synthetic admission inventory request", problem = "owned inventory question",
                outcome = "owned inventory lookup", source_refs = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, quote = source.Text } },
                missing_fields = new[] { "warehouse" }, requested_deadline_text = (string?)null, suggested_relation = (string?)null } },
            source_dispositions = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, disposition = "work" } }
        });
    }
    private static async Task RequireMutationRefusedAsync(PlatformDbContext db, GroupScope scope, CancellationToken token)
    {
        var columns = db.Model.FindEntityType(typeof(GroupOriginalAdmissionSidecarRecord))!.GetProperties().Select(x => x.Name).ToArray();
        if (columns.Length != 11) throw new InvalidOperationException();
        foreach (var commandText in columns.Select(name => $"UPDATE aioffice.GroupOriginalAdmissionSidecars SET [{name}]=[{name}] WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;")
            .Append("DELETE FROM aioffice.GroupOriginalAdmissionSidecars WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;"))
        {
            await using var connection = new SqlConnection(db.Database.GetConnectionString()); await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await using var command = new SqlCommand(commandText, connection, transaction) { CommandTimeout = 10 };
            command.Parameters.AddWithValue("@tenant", scope.TenantId); command.Parameters.AddWithValue("@company", scope.CompanyId); command.Parameters.AddWithValue("@binding", scope.SourceBindingId);
            var refused = false;
            try { await command.ExecuteNonQueryAsync(token); }
            catch (SqlException error) when (error.Number == 229 && error.Message.Contains("GroupOriginalAdmissionSidecars", StringComparison.Ordinal)) { refused = true; }
            await transaction.RollbackAsync(token); if (!refused) throw new InvalidOperationException();
        }
    }
    private sealed class CarrierClock(GroupNoteRuntimeProof.OwnedClock shared) : TimeProvider
    {
        internal bool Armed, ObservedAndRewound;
        internal DateTimeOffset ExpiredAtUtc, RewindAtUtc;
        public override DateTimeOffset GetUtcNow()
        {
            if (!Armed) return shared.GetUtcNow();
            Armed = false; ObservedAndRewound = true;
            // Return actual observed expiry, then make all later SQL clock
            // reads see the rewind before the owner can inspect again.
            shared.Current = RewindAtUtc;
            return ExpiredAtUtc;
        }
    }
    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Owned admission proof forbids HTTP.");
    }
    private sealed class OwnedRollback : IOException;
    private sealed class OwnedReplyLost : IOException;
    private sealed class AtomicProbe(GroupScope scope, GroupNoteRuntimeProof.OwnedClock clock) : DbCommandInterceptor
    {
        internal readonly GroupScope Scope = scope;
        internal readonly GroupNoteRuntimeProof.OwnedClock Clock = clock;
        internal DateTimeOffset ExpiresAtUtc;
        internal CarrierClock? CarrierClock;
        internal bool ExpireAfterFlush;
        internal bool RollbackAfterFlush, SawBothFlushed, LoseReply, ReplyLost, ForbidAdmissionReads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (ForbidAdmissionReads && command.CommandText.Contains("GroupOriginalAdmissionSidecars", StringComparison.Ordinal)) throw new InvalidOperationException();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FlushProbe(AtomicProbe probe) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if ((!probe.RollbackAfterFlush && !probe.ExpireAfterFlush) || data.Context is not PlatformDbContext db
                || !db.ChangeTracker.Entries<GroupOriginalAdmissionSidecarRecord>().Any()) return result;
            if (result != 2 || !db.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any() || !db.ChangeTracker.Entries<GroupOriginalAdmissionSidecarRecord>().Any()
                || await db.GroupAutomaticInputPlans.CountAsync(x => x.TenantId == probe.Scope.TenantId && x.CompanyId == probe.Scope.CompanyId && x.BindingId == probe.Scope.SourceBindingId, token) != 1
                || await db.GroupOriginalAdmissionSidecars.CountAsync(x => x.TenantId == probe.Scope.TenantId && x.CompanyId == probe.Scope.CompanyId && x.BindingId == probe.Scope.SourceBindingId, token) != 1)
                throw new InvalidOperationException();
            probe.SawBothFlushed = true;
            if (probe.ExpireAfterFlush)
            {
                var carrierClock = probe.CarrierClock ?? throw new InvalidOperationException();
                carrierClock.ExpiredAtUtc = probe.ExpiresAtUtc; carrierClock.RewindAtUtc = probe.Clock.Current; carrierClock.Armed = true;
                return result;
            }
            throw new OwnedRollback();
        }
    }
    private sealed class CommitProbe(AtomicProbe probe) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            if (probe.LoseReply && data.Context is PlatformDbContext db && db.ChangeTracker.Entries<GroupOriginalAdmissionSidecarRecord>().Any())
            { probe.LoseReply = false; probe.ReplyLost = true; throw new OwnedReplyLost(); }
            return Task.CompletedTask;
        }
    }
}
