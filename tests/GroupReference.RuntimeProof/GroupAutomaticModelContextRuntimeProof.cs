using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Synthetic fixed interpretation; real shipping context/read-only fence on the
// existing owned SQL fixture. No Gateway invocation, provider or tokenizer.
internal static class GroupAutomaticModelContextRuntimeProof
{
    internal const string Success = "PASS owned automatic model context exact protected Unicode source empty then four scoped AI host history closed Gateway contract two current SQL fences serializable source lock unchanged claim keys effects no provider tokenizer DI or model";

    internal static async Task RequireAsync(PlatformDbContext database, GroupExtractionWorkerBinding worker,
        TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader reader,
        GroupBatchSourcePreparation preparation, GroupBatchClaimHandle handle, GroupBrainPrivateContext brain, GroupNoteRuntimeProof.CountedKeys keys,
        Evidence evidence, Func<Task> requireOriginal, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (evidence.Scope != preparation.Scope || handle.Receipt.Scope != evidence.Scope || handle.Receipt.BatchId != preparation.BatchId
            || evidence.Armed || evidence.Completed is < 0 or >= 2
            || brain.Items.Count != (evidence.Completed == 0 ? 0 : 4)
            || database.ChangeTracker.HasChanges() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException();
        await requireOriginal();
        var state = GroupAutomaticDependencyRuntimeProof.StateFingerprint(await StateAsync());
        var connectionState = database.Database.GetDbConnection().State;
        var reads = keys.Reads; var writes = keys.Writes;
        var context = GroupAutomaticModelContext.Create(preparation, brain, "owned-synthetic-context", 64000);
        var requestId = Guid.NewGuid(); var request = context.CreateGatewayRequest(requestId); request.Validate();
        using var wire = JsonDocument.Parse(request.Input);
        var current = wire.RootElement.GetProperty("current_sources").EnumerateArray().ToArray();
        var history = wire.RootElement.GetProperty("same_group_history").EnumerateArray().ToArray();
        var source = preparation.Candidates.Single();
        if (current.Length != 1 || source.Text != "Tra cứu tồn kho 😀\uFEFF "
            || current[0].GetProperty("text").GetString() != source.Text
            || current[0].GetProperty("message_id").GetString() != source.MessageId.ToString("D")
            || current[0].GetProperty("revision").GetInt64() != source.Revision
            || current[0].GetProperty("kind").GetString() != "media_caption"
            || history.Length != brain.Items.Count || context.Manifest.Entries.Count != 4 + history.Length
            || context.Manifest.UsedTokens != Encoding.UTF8.GetByteCount(request.Input) + Encoding.UTF8.GetByteCount(request.ResponseSchema!)
            || !context.RequiresModelTokenizerQualification || request.ResponseSchema != GroupGroundedWorkProposal.ResponseSchema
            || request.Capability != AiCapability.StructuredGeneration || request.RequestId != requestId.ToString("D")
            || request.TaskId != context.BatchId.ToString("D") || request.TenantId != evidence.Scope.TenantId.ToString("D")
            || request.CompanyId != evidence.Scope.CompanyId.ToString("D")
            || request.Input.Contains("OWNED_AUTOMATIC_PRIVATE_SENTINEL", StringComparison.Ordinal)
            || request.Input.Contains(evidence.Scope.TenantId.ToString("D"), StringComparison.Ordinal)
            || request.Input.Contains(evidence.Scope.CompanyId.ToString("D"), StringComparison.Ordinal)
            || request.Input.Contains(evidence.Scope.SourceBindingId.ToString("D"), StringComparison.Ordinal)
            || preparation.Receipts.Where(x => x.Disposition == GroupSourcePreparationDisposition.Quarantined)
                .Any(x => request.Input.Contains(x.MessageId.ToString("D"), StringComparison.Ordinal)))
            throw new InvalidOperationException();
        if (history.Length != 0 && (history.Count(x => x.GetProperty("role").GetString() == "source_backed_ai_interpretation") != 2
            || history.Count(x => x.GetProperty("role").GetString() == "host_metadata_attention") != 2
            || history.Any(x => x.GetProperty("it_confirmed").GetBoolean()))) throw new InvalidOperationException();
        evidence.Observed.Clear(); evidence.Metadata = false; evidence.Payload = false; evidence.Armed = true;
        try
        {
            await new GroupAutomaticModelDependencyFence(database, worker, clock, sources, reader).RequireCurrentAsync(context, token);
            evidence.RequireComplete(history.Length != 0);
        }
        finally { evidence.Armed = false; }
        if (keys.Reads != reads || keys.Writes != writes || database.ChangeTracker.HasChanges()
            || database.Database.CurrentTransaction is not null || database.Database.GetDbConnection().State != connectionState
            || GroupAutomaticDependencyRuntimeProof.StateFingerprint(await StateAsync()) != state)
            throw new InvalidOperationException();
        GroupAutomaticRawRuntimeProof.RequireDetached(database);
        await requireOriginal(); evidence.Completed++;

        Task<GroupBatchClaimStateRecord> StateAsync() => database.GroupBatchClaimStates.AsNoTracking().SingleAsync(x =>
            x.TenantId == evidence.Scope.TenantId && x.CompanyId == evidence.Scope.CompanyId && x.BindingId == evidence.Scope.SourceBindingId
            && x.BatchId == handle.Receipt.BatchId, token);
    }

    internal sealed class Evidence(GroupScope scope)
    {
        internal GroupScope Scope { get; } = scope;
        internal bool Armed;
        internal int Completed;
        internal bool Metadata;
        internal bool Payload;
        internal HashSet<string> Observed { get; } = new(StringComparer.Ordinal);
        // The current brain reader uses one exact length-guarded CASE to
        // select ciphertext. That query observes both the cap and payload;
        // the separate metadata-before-payload reader shape remains supported.
        internal void ObserveRevision(string sql)
        {
            if (HasBoundedRevisionProjection(sql)) { Metadata = true; Payload = true; }
            else if (Regex.IsMatch(sql, @"\bDATALENGTH\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) Metadata = true;
            else { if (!Metadata) throw new InvalidOperationException(); Payload = true; }
        }
        internal void RequireComplete(bool history)
        {
            var expected = history ? SourceTables.Concat(BrainTables) : SourceTables;
            if (!Observed.SetEquals(expected) || (history && (!Metadata || !Payload))) throw new InvalidOperationException();
        }
    }
    private static readonly string[] SourceTables = ["GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts"];
    private static readonly string[] BrainTables = ["GroupCustomerRequests", "GroupRequestRevisions", "GroupRequestEvidence"];

    // EF composes both LINQ bracketed names and the shipping readers' bounded
    // raw SELECTs with bare schema/table names. Observe each before its reader
    // opens; recognizing either form never waives the real lock requirement.
    internal static string[] MaterialTables(string sql) => SourceTables.Concat(BrainTables).Where(table => Regex.IsMatch(sql,
        @"\b(?:FROM|JOIN)\s+(?:\[aioffice\]|aioffice)\s*\.\s*(?:\[" + table + @"\]|" + table + @"\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();

    internal static bool HasBoundedRevisionProjection(string sql) => Regex.IsMatch(sql,
        @"\bCASE\s+WHEN\s+DATALENGTH\s*\(\s*(?<column>\[ProtectedContent\]|ProtectedContent)\s*\)\s+BETWEEN\s+30\s+AND\s+64029\s+THEN\s+\k<column>\s+END\s+AS\s+(?:\[ProtectedContent\]|ProtectedContent\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal sealed class ReadProbe(Evidence evidence) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (!evidence.Armed) return result;
            var tables = MaterialTables(command.CommandText);
            if (tables.Length == 0) return result;
            if (command.Transaction is not { IsolationLevel: IsolationLevel.Serializable } transaction || transaction.Connection is null)
                throw new InvalidOperationException();
            await using var check = transaction.Connection.CreateCommand(); check.Transaction = transaction; check.CommandTimeout = 5;
            check.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = check.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{evidence.Scope.TenantId:N}/{evidence.Scope.CompanyId:N}/{evidence.Scope.SourceBindingId:N}";
            check.Parameters.Add(parameter);
            if (!string.Equals((string?)await check.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal))
                throw new InvalidOperationException();
            if (tables.Contains("GroupRequestRevisions")) evidence.ObserveRevision(command.CommandText);
            evidence.Observed.UnionWith(tables);
            return result;
        }
    }
}
