using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// A separate owned phase AFTER the original admission/expiry oracles. Uses
// the real fixed processor and SQL stores. Existing output is synthetic;
// no current admission service, HTTP client or model is initialized here.
internal static class GroupRestoredBatchRuntimeProof
{
    internal const string CommitLine = "PASS owned restored batch actual original SQL restored accepted result FIRST real claimed processor three automatic notes one AI two host four evidence outbox terminal and signed frontier current epoch four cold model factory";
    internal const string RecoverLine = "PASS owned restored batch separate process expired claim terminal FIRST original SELECT denied original effects and signed frontier unchanged cold model factory";
    internal static string Phase { get; private set; } = "setup";
    private static ReadProbe? activeProbe;
    internal static string ReadSignature => activeProbe?.Signature ?? "000";
    private static readonly HashSet<string> DiagnosticPhases = new(StringComparer.Ordinal)
    { "setup", "claim", "baseline", "process", "process-result", "process-reads", "model-factory", "verify",
      "verify-counts", "verify-immutable", "verify-effects", "verify-terminal", "verify-clean", "unknown" };

    // Fixed tokens only. Exception messages, inner exceptions and SQL/private
    // values are never output. Read bits are observations, not failure causes.
    internal static string FailureDiagnostic(string phase, string reads, Exception error)
    {
        var safePhase = DiagnosticPhases.Contains(phase) ? phase : "unknown";
        var safeReads = reads is { Length: 3 } && reads.All(x => x is '0' or '1') ? reads : "unknown";
        var kind = error switch
        {
            OperationCanceledException => "cancelled",
            UnauthorizedAccessException => "denied",
            SqlException sql when sql.Number == 547 => "sql-constraint",
            SqlException sql when sql.Number == 229 => "sql-permission",
            SqlException sql when sql.Number == -2 => "sql-timeout",
            SqlException sql when sql.Number == 1205 => "sql-deadlock",
            SqlException => "sql-other",
            InvalidOperationException => "invariant",
            _ => "other"
        };
        return "FAIL owned restored batch runtime phase-" + safePhase + " kind-" + kind + " reads-" + safeReads;
    }

    internal static async Task RunAsync(string mode, GroupScope scope, Guid allocationOperation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("restored-batch-commit" or "restored-batch-recover")) throw new InvalidOperationException();
        var probe = new ReadProbe();
        activeProbe = probe;
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options).AddInterceptors(probe).Options);
        Phase = "claim";
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == allocationOperation, token);
        var receipt = await db.GroupBatchClaimReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id && x.Epoch == 4, token);
        var clock = new GroupNoteRuntimeProof.OwnedClock(receipt.IssuedAtUtc);
        var claims = new GroupBatchClaimStore(db, worker, clock);
        var replay = await claims.TryAcquireAsync(scope, allocation.Id, receipt.OwnerId, receipt.OperationId, TimeSpan.FromMinutes(2), token)
            ?? throw new InvalidOperationException();
        if (!replay.WasAlreadyClaimed || replay.CurrentHandle is not { } handle || handle.Receipt.Epoch != 4
            || allocation.AfterSequence != 0 || allocation.AllocatedThroughSequence != 2 || allocation.RawRevisionCount != 2)
            throw new InvalidOperationException();
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var factoryCalls = 0;
        var processor = new GroupClaimedBatchProcessor(new GroupClaimedBatchOperations(db, worker, clock, sources, brain, keys,
            () => { factoryCalls++; Phase = "model-factory"; throw new InvalidOperationException(); }));
        Phase = "baseline";
        var immutable = await ImmutableAsync();
        var existing = await CountsAsync();
        byte[]? effects = null;
        if (mode == "restored-batch-recover")
        {
            RequireCompletedCounts(existing);
            effects = await EffectsAsync();
            clock.Current = receipt.ExpiresAtUtc.AddTicks(1);
        }
        else if (existing != (0, 0, 0, 0, 0, 0)) throw new InvalidOperationException();
        probe.Reset(); Phase = "process";
        var result = await processor.ProcessAsync(handle, token);
        Phase = "process-result";
        if (result.Scope != scope || result.BatchId != allocation.Id || result.Disposition != GroupRestoredBatchDisposition.Completed
            || result.RetryAtUtc is not null || result.RecoveredTerminal != (mode == "restored-batch-recover") || factoryCalls != 0)
            throw new InvalidOperationException();
        Phase = "process-reads"; probe.Require(mode == "restored-batch-recover");
        Phase = "verify-counts";
        RequireCompletedCounts(await CountsAsync());
        Phase = "verify-immutable";
        var afterImmutable = await ImmutableAsync();
        if (!immutable.AsSpan().SequenceEqual(afterImmutable)) throw new InvalidOperationException();
        if (effects is not null)
        {
            Phase = "verify-effects";
            var afterEffects = await EffectsAsync();
            if (!effects.AsSpan().SequenceEqual(afterEffects)) throw new InvalidOperationException();
        }
        Phase = "verify-terminal";
        var terminal = await db.GroupBatchTerminalReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        var advance = await db.GroupTerminalAdvances.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        var cursor = await db.GroupTerminalFrontierStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        if (terminal.OperationId != allocation.Id || terminal.ClaimEpoch != 4 || terminal.NoteCount != 3 || terminal.ContributorCount != 1
            || terminal.SelectedMessageCount != 2 || terminal.RawRevisionCount != 2 || advance.Version != 1 || advance.AfterSequence != 0
            || advance.ThroughSequence != 2 || cursor.Version != 1 || cursor.ThroughSequence != 2
            || cursor.LastTerminalBatchId != allocation.Id || cursor.UpdatedAtUtc != advance.AdvancedAtUtc) throw new InvalidOperationException();
        Phase = "verify-clean";
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed)
            throw new InvalidOperationException();
        Console.WriteLine(mode == "restored-batch-commit" ? CommitLine : RecoverLine);

        async Task<(int Notes, int Work, int Outbox, int Terminal, int Advances, int Cursors)> CountsAsync() =>
            (await db.GroupCustomerRequests.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
             await db.GroupWorkCommitReceipts.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
             await db.GroupNotesCommittedOutbox.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
             await db.GroupBatchTerminalReceipts.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
             await db.GroupTerminalAdvances.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
             await db.GroupTerminalFrontierStates.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token));
        async Task<byte[]> ImmutableAsync() => JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            // Sidecar reads are deliberately absent: recovery keeps the
            // existing denied SELECT contract. Parent compares those bytes.
            await db.GroupModelAttemptStarts.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.RequestId).ToArrayAsync(token),
            await db.GroupModelAttemptResults.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.RequestId).ToArrayAsync(token),
            await db.GroupModelAttemptFailures.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.RequestId).ToArrayAsync(token),
            await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.Epoch).ToArrayAsync(token),
            await db.GroupBatchClaimStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.BatchId).ToArrayAsync(token)
        });
        async Task<byte[]> EffectsAsync() => JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            await db.GroupBatchTerminalReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.BatchId).ToArrayAsync(token),
            await db.GroupTerminalAdvances.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.Version).ToArrayAsync(token),
            await db.GroupTerminalAdvanceKeys.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.KeyId).ToArrayAsync(token),
            await db.GroupTerminalFrontierStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).ToArrayAsync(token),
            await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope,
                (await db.GroupWorkCommitReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)).OperationId, token)
        });
    }

    private static void RequireCompletedCounts((int, int, int, int, int, int) counts)
    { if (counts != (3, 1, 1, 1, 1, 1)) throw new InvalidOperationException(); }

    internal sealed class ReadProbe : DbCommandInterceptor
    {
        private int originals, accepted, terminals;
        internal string Signature => (originals > 0 ? "1" : "0") + (accepted > 0 ? "1" : "0") + (terminals > 0 ? "1" : "0");
        internal void Reset() { originals = accepted = terminals = 0; }
        internal void Require(bool recovered)
        {
            if (terminals == 0 || (recovered ? originals != 0 || accepted != 0 : originals == 0 || accepted == 0)) throw new InvalidOperationException();
        }
        // The original store deliberately uses raw SqlQuery SQL. EF keeps
        // its unbracketed inner FROM text while wrapping scoped predicates.
        // These counters observe calls only; they grant no SQL authority.
        internal void ObserveSql(string sql)
        {
            if (sql.Contains("FROM aioffice.GroupAutomaticInputPlans", StringComparison.Ordinal)) originals++;
            if (sql.Contains("FROM [aioffice].[GroupModelAttemptResults]", StringComparison.Ordinal)) accepted++;
            if (sql.Contains("FROM [aioffice].[GroupBatchTerminalReceipts]", StringComparison.Ordinal)) terminals++;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            ObserveSql(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
