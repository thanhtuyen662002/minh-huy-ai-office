extern alias RuntimeWorker;

using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Fresh owned two-text fixture, existing Gateway and synthetic adapter/catalog only. No original, accepted result or effect is
// seeded. The actual fixed processor owns first creation and completion.
internal static class GroupFirstModelBatchRuntimeProof
{
    internal const string CommitLine = "PASS owned first synthetic model batch actual missing original real claimed processor atomic V3 measured sidecar one admitted synthetic Gateway execution encrypted accepted result combined two source AI interpretation evidence outbox terminal signed frontier";
    internal const string RecoverLine = "PASS owned first synthetic model batch separate process expired claim terminal FIRST original accepted sidecar companion SELECT denied immutable full graph signed frontier cold Gateway factory";
    internal static string Phase { get; private set; } = "setup";
    private static readonly string[] Tables =
    [
        "GroupAutomaticInputPlans", "GroupOriginalAdmissionSidecars", "GroupModelAttemptAdmissions",
        "GroupModelAttemptStarts", "GroupModelAttemptResults", "GroupModelAttemptFailures",
        "GroupWorkCommitReceipts", "GroupWorkSourceDispositions", "GroupWorkRawDispositions",
        "GroupCustomerRequests", "GroupRequestRevisions", "GroupRequestEvidence",
        "GroupNotesCommittedOutbox", "GroupNotesCommittedItems", "GroupBatchTerminalReceipts",
        "GroupTerminalAdvances", "GroupTerminalAdvanceKeys", "GroupTerminalFrontierStates"
    ];
    private static readonly int[] CompletedCounts = [1, 1, 1, 1, 1, 0, 1, 2, 2, 1, 1, 2, 1, 1, 1, 1, 1, 1];

    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("first-model-commit" or "first-model-recover")) throw new InvalidOperationException();
        var recovered = mode == "first-model-recover";
        var probe = new ReadProbe();
        var execution = new GroupOriginalAdmissionRuntimeProof.AdmittedExecutionSqlProbe();
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options).AddInterceptors(probe, execution).Options);
        Phase = "allocation";
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        if (allocation.AfterSequence != 0 || allocation.AllocatedThroughSequence != 2 || allocation.RawRevisionCount != 2) throw new InvalidOperationException();
        var clock = new GroupNoteRuntimeProof.OwnedClock(allocation.AllocatedAtUtc);
        var claims = new GroupBatchClaimStore(db, worker, clock);
        GroupBatchClaimResult? acquired;
        if (recovered)
        {
            var receipt = await db.GroupBatchClaimReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
            if (receipt.Epoch != 1) throw new InvalidOperationException();
            clock.Current = receipt.IssuedAtUtc;
            acquired = await claims.TryAcquireAsync(scope, allocation.Id, receipt.OwnerId, receipt.OperationId, TimeSpan.FromMinutes(2), token);
            if (acquired is not { WasAlreadyClaimed: true }) throw new InvalidOperationException();
        }
        else
        {
            await RequireCountsAsync(false);
            if (await db.GroupBatchClaimReceipts.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
            acquired = await claims.TryAcquireAsync(scope, allocation.Id, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token);
            if (acquired is not { WasAlreadyClaimed: false }) throw new InvalidOperationException();
        }
        if (acquired?.CurrentHandle is not { } handle || handle.Receipt.Epoch != 1) throw new InvalidOperationException();
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var calls = 0; SyntheticAdapter? adapter = null; StructuredModelAdmission? gate = null;
        if (!recovered)
        {
            var members = await claims.ReadCurrentAllocationAsync(handle, token);
            var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle,
                members.Revisions.Select(x => x.Metadata.MessageId).ToArray(), token));
            if (preparation.Candidates.Count != 2 || preparation.Receipts.Count != 2
                || preparation.Receipts.Any(x => x.Disposition != GroupSourcePreparationDisposition.ModelText || x.HasUnsupportedMedia)) throw new InvalidOperationException();
            adapter = new(db, execution, Output(preparation));
            var gateway = new OrderedFailoverAiGateway(adapter);
            var metadata = gateway.DescribeStructuredGateway(); var route = metadata.Routes.Single();
            var candidate = new StructuredModelProfileCandidate(new AiModelProfile(route.ProviderId, route.ConfiguredModel,
                new HashSet<AiCapability> { AiCapability.StructuredGeneration }, 1, 0, 1), metadata.MetadataSha256, 0,
                route.EndpointIdentitySha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GroupGroundedWorkProposal.ResponseSchema))),
                StructuredTextTokenCounter.Version, 128000, 1024, 2048, [route.ConfiguredModel], new('A', 64));
            gate = new(gateway, StructuredModelProfileCatalog.ForSyntheticControls(candidate), new BoundedStructuredTextTokenCounter(), clock);
        }
        var processor = new GroupClaimedBatchProcessor(new GroupClaimedBatchOperations(db, worker, clock, sources, brain, keys,
            () => { calls++; return gate ?? throw new InvalidOperationException(); }));
        if (recovered) { await RequireCountsAsync(true); clock.Current = handle.Receipt.ExpiresAtUtc.AddTicks(1); }
        probe.Reset(); execution.Armed = !recovered; Phase = "process";
        var result = await processor.ProcessAsync(handle, token);
        probe.Require(recovered);
        if (!recovered) execution.RequireCompleted();
        execution.Armed = false;
        if (calls != (recovered ? 0 : 2) || (adapter?.Executions ?? 0) != (recovered ? 0 : 1) || result.Scope != scope || result.BatchId != allocation.Id || result.RetryAtUtc is not null
            || result.Disposition != GroupRestoredBatchDisposition.Completed || result.RecoveredTerminal != recovered) throw new InvalidOperationException();
        Phase = "verify";
        await RequireCountsAsync(true);
        if (!recovered)
        {
            var store = new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain);
            var original = await store.ReadAsync(handle, token) ?? throw new InvalidOperationException();
            var sidecar = await store.ReadOriginalAdmissionAsync(handle, original, token) ?? throw new InvalidOperationException();
            if (original.WireFormatVersion != 3 || original.HistoryOrder.Count != 0 || original.Chunks.Count != 1
                || !original.Chunks[0].RequiresModel || sidecar.Entries.Count != 1 || sidecar.Entries[0].Snapshot is null
                || sidecar.OriginalClaimEpoch != 1 || sidecar.OriginalClaimOperationId != handle.Receipt.OperationId) throw new InvalidOperationException();
            var receipt = await db.GroupWorkCommitReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
            var dispositions = await db.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id).ToArrayAsync(token);
            var note = await db.GroupCustomerRequests.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OriginBatchId == allocation.Id, token);
            var readback = await brain.ReadAsync(handle, [note.Id], [], token);
            if (readback.Items.Count != 1) throw new InvalidOperationException();
            var item = readback.Items[0]; var payload = GroupBrainPayloadCodec.DecodeAiNote(item.Content);
            var currentAllocation = await claims.ReadCurrentAllocationAsync(handle, token);
            if (currentAllocation.Revisions.Count != 2 || currentAllocation.Revisions.Any(x => x.Metadata.Kind != GroupSourceEventKind.NewText)) throw new InvalidOperationException();
            var expected = currentAllocation.Revisions.Select(x => new GroupHostAttentionReference(x.Metadata.MessageId, x.Metadata.Revision))
                .OrderBy(x => x.MessageId).ThenBy(x => x.Revision).ToArray();
            if (receipt.Outcome != GroupWorkCommitOutcome.Notes || receipt.NoteCount != 1 || receipt.SelectedMessageCount != 2
                || dispositions.Length != 2 || dispositions.Any(x => x.Outcome != GroupWorkSourceOutcome.Work)
                || item.RecordId != note.Id || item.Revision != 1 || item.IsItConfirmed
                || item.Origin != GroupRequestRevisionOrigin.AiExtracted || item.VerificationLevel != GroupRequestVerificationLevel.SourceBackedAiInterpretation
                || item.BusinessStatus != GroupNoteBusinessStatus.NeedsClarification || payload.Kind != GroupWorkProposalKind.NeedsClarification
                || !payload.Evidence.Select(x => new GroupHostAttentionReference(x.MessageId, x.Revision)).OrderBy(x => x.MessageId).ThenBy(x => x.Revision).SequenceEqual(expected)) throw new InvalidOperationException();
            await brain.RequireCurrentAsync(readback, token);
            var evidence = await db.GroupRequestEvidence.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.RequestId == note.Id).ToArrayAsync(token);
            var outbox = await db.GroupNotesCommittedOutbox.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
            if (evidence.Length != 2 || evidence.Any(x => x.Kind != GroupRequestEvidenceKind.LiteralSourceQuote || x.RequestRevision != 1)
                || !evidence.Select(x => new GroupHostAttentionReference(x.MessageId, x.MessageRevision)).OrderBy(x => x.MessageId)
                    .ThenBy(x => x.Revision).SequenceEqual(expected) || outbox.NoteCount != 1 || outbox.IsHistoricalBackfill
                || outbox.PublishAttempts != 0 || outbox.PublishedAtUtc is not null) throw new InvalidOperationException();
        }
        var terminal = await db.GroupBatchTerminalReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        var advance = await db.GroupTerminalAdvances.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        var cursor = await db.GroupTerminalFrontierStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        if (terminal.OperationId != allocation.Id || terminal.ClaimEpoch != 1 || terminal.NoteCount != 1 || terminal.ContributorCount != 1
            || terminal.SelectedMessageCount != 2 || terminal.RawRevisionCount != 2 || advance.Version != 1 || advance.AfterSequence != 0
            || advance.ThroughSequence != 2 || cursor.Version != 1 || cursor.ThroughSequence != 2 || cursor.LastTerminalBatchId != allocation.Id
            || cursor.UpdatedAtUtc != advance.AdvancedAtUtc || db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null
            || db.Database.GetDbConnection().State != ConnectionState.Closed) throw new InvalidOperationException();
        Console.WriteLine(recovered ? RecoverLine : CommitLine);

        async Task RequireCountsAsync(bool complete)
        {
            for (var i = 0; i < Tables.Length; i++)
            {
                // Recovery is run with these four SELECTs denied. The parent
                // separately compares their full original graphs.
                if (recovered && i is 0 or 1 or 2 or 4) continue;
                // The table comes only from the private fixed list; values remain parameters.
                var query = "SELECT COUNT(*) AS Value FROM aioffice." + Tables[i] + " WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding";
                var count = await db.Database.SqlQueryRaw<int>(query,
                    new SqlParameter("@tenant", scope.TenantId), new SqlParameter("@company", scope.CompanyId),
                    new SqlParameter("@binding", scope.SourceBindingId)).SingleAsync(token);
                if (count != (complete ? CompletedCounts[i] : 0)) throw new InvalidOperationException();
            }
        }
    }

    private static string Output(GroupBatchSourcePreparation preparation) => JsonSerializer.Serialize(new
    {
        version = GroupGroundedWorkProposal.FormatVersion,
        notes = new[] { new { type = "needs_clarification", title = "owned combined synthetic inventory question", problem = "owned inventory question",
            outcome = "owned inventory lookup", source_refs = preparation.Candidates.Select(x => new { message_id = x.MessageId.ToString("D"), revision = x.Revision, quote = x.Text }),
            missing_fields = new[] { "warehouse" }, requested_deadline_text = (string?)null, suggested_relation = (string?)null } },
        source_dispositions = preparation.Candidates.Select(x => new { message_id = x.MessageId.ToString("D"), revision = x.Revision, disposition = "work" })
    });

    private sealed class SyntheticAdapter(PlatformDbContext db, GroupOriginalAdmissionRuntimeProof.AdmittedExecutionSqlProbe probe, string output)
        : IAiProviderAdapter, IAiStructuredProviderMetadataSource
    {
        internal int Executions { get; private set; }
        public string ProviderId => "owned-synthetic-first-model";
        public bool Supports(AiCapability capability) => capability == AiCapability.StructuredGeneration;
        public AiStructuredRouteMetadata DescribeStructuredRoute() => new(ProviderId, ProviderId, new('E', 64), 2048,
            TimeSpan.FromSeconds(60), new("owned-synthetic-first-model-v1", 128000, 32000, 64000, 262144, 32, 10, 4));
        public async Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); probe.ProviderStarted(OutsideSql()); Executions++;
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); probe.ProviderReturned(OutsideSql());
            return new(request.RequestId, request.Capability, output, ProviderId, 123, 7) { ProviderId = ProviderId };
        }
        private bool OutsideSql() => db.Database.CurrentTransaction is null && System.Transactions.Transaction.Current is null
            && !db.ChangeTracker.HasChanges() && db.Database.GetDbConnection().State == ConnectionState.Closed;
    }

    internal sealed class ReadProbe : DbCommandInterceptor
    {
        private int originals, accepted, terminals;
        internal void Reset() { originals = accepted = terminals = 0; }
        internal void ObserveSql(string sql)
        {
            if (sql.Contains("FROM aioffice.GroupAutomaticInputPlans", StringComparison.Ordinal)) originals++;
            if (sql.Contains("FROM [aioffice].[GroupModelAttemptResults]", StringComparison.Ordinal)) accepted++;
            if (sql.Contains("FROM [aioffice].[GroupBatchTerminalReceipts]", StringComparison.Ordinal)) terminals++;
        }
        internal void Require(bool recovered)
        {
            if (terminals == 0 || (recovered ? originals != 0 || accepted != 0 : originals == 0 || accepted == 0)) throw new InvalidOperationException();
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            ObserveSql(command.CommandText); return ValueTask.FromResult(result);
        }
    }
}
