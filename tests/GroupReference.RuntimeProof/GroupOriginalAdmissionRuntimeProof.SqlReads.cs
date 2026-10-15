using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupOriginalAdmissionRuntimeProof
{
    internal const string SqlReadLine = "PASS owned original admission actual SQL exact original sidecar select own row and four single selector counterfactual empty reads same locked transaction unchanged original decoder zero keys or provider";

    private static async Task RequireSqlReadBoundariesAsync(GroupScope scope, GroupBatchClaimHandle handle,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, TimeProvider clock, CancellationToken token)
    {
        var probe = new SidecarReadProbe(scope, handle.Receipt.BatchId);
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options).AddInterceptors(probe).Options);
        probe.Context = database;
        var keys = new GroupNoteRuntimeProof.CountedKeys(database, scope);
        var sources = new GroupBatchSourceReader(database, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(database, worker, clock, keys, new());
        var store = new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain);
        Phase = "sql-read-original";
        var plan = await store.ReadAsync(handle, token) ?? throw new InvalidOperationException();
        Phase = "sql-read-predicates"; probe.Armed = true;
        var sidecar = await store.ReadOriginalAdmissionAsync(handle, plan, token) ?? throw new InvalidOperationException();
        sidecar.RequirePlan(plan);
        if (probe.Observed != 1 || probe.Comparisons != 5 || sidecar.OriginalClaimEpoch != 2
            || sidecar.OriginalClaimOperationId == handle.Receipt.OperationId || sidecar.Entries.Count != plan.Chunks.Count
            || keys.Reads != 0 || keys.Writes != 0) throw new InvalidOperationException();
        RequireClean(database);
    }

    // This validates the literal shipping command, not a separately authored
    // equivalent SELECT. It grants no foreign authority or SQL login isolation.
    internal static IReadOnlyDictionary<string, SqlParameter> ReadSelectorParameters(SqlCommand command, GroupScope scope, Guid batch)
    {
        if (command.CommandType != CommandType.Text || command.Parameters.Count != 4) throw new InvalidOperationException();
        var sql = Regex.Replace(command.CommandText, @"\s+", " ").Trim();
        if (!Regex.IsMatch(sql, @"^SELECT TOP\(2\) ", RegexOptions.CultureInvariant)
            || Regex.Matches(sql, @"\bTOP\(2\)", RegexOptions.CultureInvariant).Count != 1
            || !sql.Contains("FROM aioffice.GroupOriginalAdmissionSidecars", StringComparison.Ordinal)) throw new InvalidOperationException();
        var shape = Regex.Match(sql, @" FROM \((.+)\) AS (\[[A-Za-z_][A-Za-z0-9_]*\]) WHERE (.+)$", RegexOptions.CultureInvariant);
        const string payload = "SELECT TenantId,CompanyId,BindingId,BatchId,FormatVersion,PlanSha256,SidecarSha256,OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc,"
            + "CASE WHEN DATALENGTH([Sidecar]) BETWEEN 211 AND 1010000 THEN [Sidecar] END AS [Sidecar] FROM aioffice.GroupOriginalAdmissionSidecars";
        if (!shape.Success || Regex.Replace(shape.Groups[1].Value, @"\s+", "") != Regex.Replace(payload, @"\s+", "")) throw new InvalidOperationException();
        var clauses = shape.Groups[3].Value.Split(" AND ", StringSplitOptions.None);
        if (clauses.Length != 4) throw new InvalidOperationException();
        var expected = new Dictionary<string, Guid>(StringComparer.Ordinal)
        { ["TenantId"] = scope.TenantId, ["CompanyId"] = scope.CompanyId, ["BindingId"] = scope.SourceBindingId, ["BatchId"] = batch };
        var answer = new Dictionary<string, SqlParameter>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal); var alias = shape.Groups[2].Value;
        foreach (var clause in clauses)
        {
            var match = Regex.Match(clause.Trim(), @"^(\[[A-Za-z_][A-Za-z0-9_]*\])\.\[(TenantId|CompanyId|BindingId|BatchId)\] = (@[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);
            if (!match.Success || alias != match.Groups[1].Value) throw new InvalidOperationException();
            var column = match.Groups[2].Value; var name = match.Groups[3].Value;
            if (!names.Add(name) || !command.Parameters.Contains(name) || command.Parameters[name] is not SqlParameter parameter
                || parameter.SqlDbType != SqlDbType.UniqueIdentifier || parameter.Direction != ParameterDirection.Input
                || parameter.Value is not Guid value || value == Guid.Empty || value != expected[column]
                || !answer.TryAdd(column, parameter)) throw new InvalidOperationException();
        }
        if (answer.Count != 4) throw new InvalidOperationException();
        return answer;
    }

    private sealed class SidecarReadProbe(GroupScope scope, Guid batch) : DbCommandInterceptor
    {
        internal PlatformDbContext? Context;
        internal bool Armed;
        internal int Observed, Comparisons;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (!Armed || !command.CommandText.Contains("FROM aioffice.GroupOriginalAdmissionSidecars", StringComparison.Ordinal)) return result;
            if (++Observed != 1 || result.HasResult || Context is null || !ReferenceEquals(data.Context, Context) || command is not SqlCommand original
                || original.Connection is not { State: ConnectionState.Open } connection
                || original.Transaction is not { IsolationLevel: IsolationLevel.Serializable } transaction
                || !ReferenceEquals(transaction, Context.Database.CurrentTransaction?.GetDbTransaction())
                || new SqlConnectionStringBuilder(connection.ConnectionString).MultipleActiveResultSets) throw new InvalidOperationException();
            var selectors = ReadSelectorParameters(original, scope, batch);
            var columns = new[] { "TenantId", "CompanyId", "BindingId", "BatchId" };
            for (var index = -1; index < columns.Length; index++)
            {
                await using var clone = new SqlCommand(original.CommandText, connection, transaction)
                { CommandType = original.CommandType, CommandTimeout = original.CommandTimeout };
                foreach (SqlParameter parameter in original.Parameters) clone.Parameters.Add((SqlParameter)((ICloneable)parameter).Clone());
                if (index >= 0)
                {
                    var parameter = clone.Parameters[selectors[columns[index]].ParameterName];
                    Guid other; do { other = Guid.NewGuid(); } while (other == (Guid)parameter.Value);
                    parameter.Value = other;
                }
                var rows = 0;
                await using (var reader = await clone.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token))
                {
                    while (await reader.ReadAsync(token)) if (++rows > 1) throw new InvalidOperationException();
                    if (await reader.NextResultAsync(token)) throw new InvalidOperationException();
                }
                if (rows != (index == -1 ? 1 : 0)) throw new InvalidOperationException();
                Comparisons++;
            }
            // Direct ADO commands bypass EF interception. All five clones are
            // disposed before the original command is executed unchanged.
            return result;
        }
    }
}
