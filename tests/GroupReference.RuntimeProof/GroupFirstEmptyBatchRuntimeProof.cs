using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Fresh owned empty-text fixture. No original, accepted result or effect is
// seeded. The actual fixed processor owns first creation and completion.
internal static class GroupFirstEmptyBatchRuntimeProof
{
    internal const string CommitLine = "PASS owned first empty batch actual missing original real claimed processor atomic V3 sidecar no work two dispositions terminal signed frontier cold model factory";
    internal const string RecoverLine = "PASS owned first empty batch separate process expired claim terminal FIRST original SELECT denied immutable no work original sidecar signed frontier cold model factory";
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
    private static readonly int[] CompletedCounts = [1, 1, 0, 0, 0, 0, 1, 2, 2, 0, 0, 0, 0, 0, 1, 1, 1, 1];

    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("first-empty-commit" or "first-empty-recover")) throw new InvalidOperationException();
        var recovered = mode == "first-empty-recover";
        var probe = new ReadProbe();
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options).AddInterceptors(probe).Options);
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
        var calls = 0;
        var processor = new GroupClaimedBatchProcessor(new GroupClaimedBatchOperations(db, worker, clock, sources, brain, keys,
            () => { calls++; throw new InvalidOperationException(); }));
        if (recovered) { await RequireCountsAsync(true); clock.Current = handle.Receipt.ExpiresAtUtc.AddTicks(1); }
        probe.Reset(); Phase = "process";
        var result = await processor.ProcessAsync(handle, token);
        probe.Require(recovered);
        if (calls != 0 || result.Scope != scope || result.BatchId != allocation.Id || result.RetryAtUtc is not null
            || result.Disposition != GroupRestoredBatchDisposition.Completed || result.RecoveredTerminal != recovered) throw new InvalidOperationException();
        Phase = "verify";
        await RequireCountsAsync(true);
        if (!recovered)
        {
            var store = new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain);
            var original = await store.ReadAsync(handle, token) ?? throw new InvalidOperationException();
            var sidecar = await store.ReadOriginalAdmissionAsync(handle, original, token) ?? throw new InvalidOperationException();
            if (original.WireFormatVersion != 3 || original.HistoryOrder.Count != 0 || original.Chunks.Count != 1
                || original.Chunks[0].RequiresModel || sidecar.Entries.Count != 1 || sidecar.Entries[0].Snapshot is not null
                || sidecar.OriginalClaimEpoch != 1 || sidecar.OriginalClaimOperationId != handle.Receipt.OperationId) throw new InvalidOperationException();
        }
        var terminal = await db.GroupBatchTerminalReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        var advance = await db.GroupTerminalAdvances.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        var cursor = await db.GroupTerminalFrontierStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        if (terminal.OperationId != allocation.Id || terminal.ClaimEpoch != 1 || terminal.NoteCount != 0 || terminal.ContributorCount != 1
            || terminal.SelectedMessageCount != 2 || terminal.RawRevisionCount != 2 || advance.Version != 1 || advance.AfterSequence != 0
            || advance.ThroughSequence != 2 || cursor.Version != 1 || cursor.ThroughSequence != 2 || cursor.LastTerminalBatchId != allocation.Id
            || cursor.UpdatedAtUtc != advance.AdvancedAtUtc || db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null
            || db.Database.GetDbConnection().State != ConnectionState.Closed) throw new InvalidOperationException();
        Console.WriteLine(recovered ? RecoverLine : CommitLine);

        async Task RequireCountsAsync(bool complete)
        {
            for (var i = 0; i < Tables.Length; i++)
            {
                // Recovery is run with these three SELECTs denied. The parent
                // separately compares their full original graphs.
                if (recovered && i < 3) continue;
                // The table comes only from the private fixed list; values remain parameters.
                var query = "SELECT COUNT(*) AS Value FROM aioffice." + Tables[i] + " WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding";
                var count = await db.Database.SqlQueryRaw<int>(query,
                    new SqlParameter("@tenant", scope.TenantId), new SqlParameter("@company", scope.CompanyId),
                    new SqlParameter("@binding", scope.SourceBindingId)).SingleAsync(token);
                if (count != (complete ? CompletedCounts[i] : 0)) throw new InvalidOperationException();
            }
        }
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
            if (accepted != 0 || terminals == 0 || (recovered ? originals != 0 : originals == 0)) throw new InvalidOperationException();
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            ObserveSql(command.CommandText); return ValueTask.FromResult(result);
        }
    }
}
