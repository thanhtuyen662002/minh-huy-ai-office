using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Additive owned native observation. Successful current dependencies do not
// establish business effects, completion, a frontier or model quality.
internal static class GroupAutomaticDependencyRuntimeProof
{
    internal static async Task RequireAsync(PlatformDbContext database, GroupBatchClaimHandle handle,
        GroupExtractionWorkerBinding worker, TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain,
        GroupNoteRuntimeProof.CountedKeys keys, Evidence evidence, Func<Task> requireOriginal, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (evidence.Scope != handle.Receipt.Scope || evidence.Armed || evidence.Completed is < 0 or >= 3
            || database.ChangeTracker.HasChanges() || database.Database.CurrentTransaction is not null) throw new InvalidOperationException();
        var original = await StateAsync();
        if (original.Epoch != handle.Receipt.Epoch || original.OwnerId != handle.Receipt.OwnerId
            || original.OperationId != handle.Receipt.OperationId || original.IssuedAtUtc != handle.Receipt.IssuedAtUtc
            || original.ExpiresAtUtc != handle.Receipt.ExpiresAtUtc || original.ExpiryObservedAtUtc is not null) throw new InvalidOperationException();
        var fingerprint = StateFingerprint(original); var reads = keys.Reads; var writes = keys.Writes;
        var connectionState = database.Database.GetDbConnection().State;
        evidence.Observed.Clear(); evidence.ObservedEffects.Clear();
        evidence.RevisionMetadataObserved = false; evidence.RevisionPayloadObserved = false; evidence.Armed = true;
        try
        {
            await new GroupAutomaticBatchDependencyStore(database, worker, clock, sources, brain).RequireCurrentAsync(handle, token);
            evidence.RequireComplete();
            evidence.RequireEffectsComplete();
        }
        finally { evidence.Armed = false; }
        if (keys.Reads != reads || keys.Writes != writes || database.ChangeTracker.HasChanges()
            || database.Database.CurrentTransaction is not null || database.Database.GetDbConnection().State != connectionState
            || StateFingerprint(await StateAsync()) != fingerprint) throw new InvalidOperationException();
        GroupAutomaticRawRuntimeProof.RequireDetached(database);
        await requireOriginal(); evidence.Completed++;

        Task<GroupBatchClaimStateRecord> StateAsync() => database.GroupBatchClaimStates.AsNoTracking().SingleAsync(x =>
            x.TenantId == evidence.Scope.TenantId && x.CompanyId == evidence.Scope.CompanyId && x.BindingId == evidence.Scope.SourceBindingId
            && x.BatchId == handle.Receipt.BatchId, token);
    }

    internal static string StateFingerprint(GroupBatchClaimStateRecord value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal sealed class Evidence(GroupScope scope)
    {
        internal GroupScope Scope { get; } = scope;
        internal bool Armed;
        internal int Completed;
        internal HashSet<string> Observed { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> ObservedEffects { get; } = new(StringComparer.Ordinal);
        internal bool RevisionMetadataObserved;
        internal bool RevisionPayloadObserved;
        internal void RequireComplete()
        { if (!Observed.SetEquals(Tables)) throw new InvalidOperationException(); }
        internal void RequireEffectsComplete()
        {
            if (!ObservedEffects.SetEquals(OriginalEffectTables) || !RevisionMetadataObserved || !RevisionPayloadObserved)
                throw new InvalidOperationException();
        }
        internal void ObserveEffects(string sql)
        {
            var tables = EffectTables(sql);
            if (tables.Contains("GroupRequestRevisions"))
            {
                if (Regex.IsMatch(sql, @"\bDATALENGTH\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    RevisionMetadataObserved = true;
                else
                {
                    if (!RevisionMetadataObserved) throw new InvalidOperationException();
                    RevisionPayloadObserved = true;
                }
            }
            ObservedEffects.UnionWith(tables);
        }
    }
    private static readonly string[] Tables = ["GroupWorkCommitReceipts", "GroupWorkSourceDispositions", "GroupWorkRawDispositions", "GroupMessageRevisions"];
    private static readonly string[] OriginalEffectTables = ["GroupBatchClaimReceipts", "GroupCustomerRequests", "GroupRequestRevisions",
        "GroupRequestEvidence", "GroupNotesCommittedOutbox", "GroupNotesCommittedItems"];

    internal static string[] MaterialTables(string sql) => Tables.Where(table => Regex.IsMatch(sql,
        @"\b(?:FROM|JOIN)\s+\[aioffice\]\.\[" + table + @"\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
    internal static string[] EffectTables(string sql) => OriginalEffectTables.Where(table => Regex.IsMatch(sql,
        @"\b(?:FROM|JOIN)\s+\[aioffice\]\.\[" + table + @"\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();

    internal sealed class ReadProbe(Evidence evidence) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (!evidence.Armed) return result;
            var tables = MaterialTables(command.CommandText);
            if (tables.Length == 0 && EffectTables(command.CommandText).Length == 0) return result;
            if (command.Transaction is not { IsolationLevel: IsolationLevel.Serializable } transaction || transaction.Connection is null)
                throw new InvalidOperationException();
            // Observe the real session lock before its reader opens, so MARS
            // is neither required nor enabled by this proof.
            await using var check = transaction.Connection.CreateCommand(); check.Transaction = transaction; check.CommandTimeout = 5;
            check.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = check.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{evidence.Scope.TenantId:N}/{evidence.Scope.CompanyId:N}/{evidence.Scope.SourceBindingId:N}";
            check.Parameters.Add(parameter);
            if (!string.Equals((string?)await check.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
            foreach (var table in tables) evidence.Observed.Add(table);
            evidence.ObserveEffects(command.CommandText);
            return result;
        }
    }
}
