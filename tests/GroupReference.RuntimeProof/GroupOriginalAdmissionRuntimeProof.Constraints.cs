using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupOriginalAdmissionRuntimeProof
{
    // These exact strings are used below and parsed by the offline SQL
    // regression. PLAN is reserved in T-SQL; delimit its existing column.
    internal const string ConstraintOriginalInsertSql = """
        INSERT INTO aioffice.GroupAutomaticInputPlans
          (TenantId,CompanyId,BindingId,BatchId,FormatVersion,PlanSha256,[Plan],OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc)
        VALUES (@tenant,@company,@binding,@batch,3,@planHash,@plan,@claimOperation,@claimEpoch,@created);
        """;
    internal const string ConstraintAdmissionInsertSql = """
        INSERT INTO aioffice.GroupOriginalAdmissionSidecars
          (TenantId,CompanyId,BindingId,BatchId,FormatVersion,PlanSha256,SidecarSha256,Sidecar,OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc)
        VALUES (@tenant,@company,@binding,@batch,@version,@planHash,@sidecarHash,@sidecar,@claimOperation,@claimEpoch,@created);
        """;
    // Runtime INSERT privileges, original owned allocation/claim, exact valid
    // bytes. Every case rolls back; no operator permission change or durable
    // legacy-plan-only intermediate is needed to exercise the physical FKs.
    private static async Task RequireConstraintBoundariesAsync(PlatformDbContext database, GroupScope scope,
        GroupBatchClaimHandle handle, GroupAutomaticInputPlan plan, GroupOriginalAdmissionSidecar sidecar,
        DateTimeOffset now, CancellationToken token)
    {
        var cases = new (string Name, string? Constraint, bool WithPlan, Action<SqlParameterCollection> Change)[]
        {
            ("valid-pair", null, true, _ => { }),
            ("missing-plan", "FK_GroupOriginalAdmissionSidecars_OriginalPlan", false, _ => { }),
            ("wrong-plan-hash", "FK_GroupOriginalAdmissionSidecars_OriginalPlan", true, p => p["@planHash"].Value = new string(plan.Sha256[0] == 'B' ? 'C' : 'B', 64)),
            ("wrong-claim-operation", "FK_GroupOriginalAdmissionSidecars_OriginalClaim", true, p => p["@claimOperation"].Value = Guid.NewGuid()),
            ("wrong-claim-epoch", "FK_GroupOriginalAdmissionSidecars_OriginalClaim", true, p => p["@claimEpoch"].Value = handle.Receipt.Epoch + 1),
            ("foreign-tenant", "FK_GroupOriginalAdmissionSidecars_", true, p => p["@tenant"].Value = Guid.NewGuid()),
            ("foreign-company", "FK_GroupOriginalAdmissionSidecars_", true, p => p["@company"].Value = Guid.NewGuid()),
            ("foreign-binding", "FK_GroupOriginalAdmissionSidecars_", true, p => p["@binding"].Value = Guid.NewGuid()),
            ("format-version", "CK_GroupOriginalAdmissionSidecars_Format", true, p => p["@version"].Value = 2),
            ("short-payload", "CK_GroupOriginalAdmissionSidecars_Format", true, p => p["@sidecar"].Value = new byte[210]),
            ("oversized-payload", "CK_GroupOriginalAdmissionSidecars_Format", true, p => p["@sidecar"].Value = new byte[1_010_001]),
            ("empty-operation", "CK_GroupOriginalAdmissionSidecars_Identity", true, p => p["@claimOperation"].Value = Guid.Empty),
            ("zero-epoch", "CK_GroupOriginalAdmissionSidecars_Identity", true, p => p["@claimEpoch"].Value = 0L),
            ("negative-epoch", "CK_GroupOriginalAdmissionSidecars_Identity", true, p => p["@claimEpoch"].Value = -1L),
            ("lowercase-plan-hash", "CK_GroupOriginalAdmissionSidecars_Identity", true, p => p["@planHash"].Value = new string('a', 64)),
            ("nonhex-sidecar-hash", "CK_GroupOriginalAdmissionSidecars_Identity", true, p => p["@sidecarHash"].Value = new string('Z', 64)),
            ("lowercase-sidecar-hash", "CK_GroupOriginalAdmissionSidecars_Identity", true, p => p["@sidecarHash"].Value = new string('a', 64)),
            ("non-UTC-time", "CK_GroupOriginalAdmissionSidecars_Time", true, p => p["@created"].Value = now.ToOffset(TimeSpan.FromHours(7)))
        };
        if (cases.Length != 18 || now.Offset != TimeSpan.Zero || sidecar.Scope != scope
            || sidecar.BatchId != handle.Receipt.BatchId || sidecar.PlanSha256 != plan.Sha256)
            throw new InvalidOperationException();
        var originalBytes = plan.Encode(); var sidecarBytes = sidecar.Encode();
        await using var connection = new SqlConnection(database.Database.GetConnectionString());
        await connection.OpenAsync(token);
        foreach (var test in cases)
        {
            Phase = "constraint-" + test.Name;
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            try
            {
                if (test.WithPlan)
                {
                    await using var original = new SqlCommand(ConstraintOriginalInsertSql, connection, transaction) { CommandTimeout = 10 };
                    IdentityParameters(original.Parameters);
                    original.Parameters.Add("@plan", SqlDbType.VarBinary, -1).Value = originalBytes;
                    if (await original.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException();
                }
                await using var admission = new SqlCommand(ConstraintAdmissionInsertSql, connection, transaction) { CommandTimeout = 10 };
                IdentityParameters(admission.Parameters);
                admission.Parameters.Add("@version", SqlDbType.Int).Value = 1;
                admission.Parameters.Add("@sidecarHash", SqlDbType.Char, 64).Value = sidecar.Sha256;
                admission.Parameters.Add("@sidecar", SqlDbType.VarBinary, -1).Value = sidecarBytes;
                test.Change(admission.Parameters);
                var refused = false;
                try
                {
                    if (await admission.ExecuteNonQueryAsync(token) != 1 || test.Constraint is not null) throw new InvalidOperationException();
                }
                catch (SqlException error) when (test.Constraint is not null
                    && IsExpectedConstraintFailure(test.Name, error.Number, error.Message))
                { refused = true; }
                if (refused != (test.Constraint is not null)) throw new InvalidOperationException();
                // Counts run under this same SQL transaction, before rollback.
                if (await CountAsync("GroupAutomaticInputPlans", transaction) != (test.WithPlan ? 1 : 0)
                    || await CountAsync("GroupOriginalAdmissionSidecars", transaction) != (refused ? 0 : 1))
                    throw new InvalidOperationException();
            }
            finally { await transaction.RollbackAsync(token); }
            if (await CountAsync("GroupAutomaticInputPlans", null) != 0
                || await CountAsync("GroupOriginalAdmissionSidecars", null) != 0) throw new InvalidOperationException();
            RequireClean(database);
        }

        void IdentityParameters(SqlParameterCollection parameters)
        {
            parameters.Add("@tenant", SqlDbType.UniqueIdentifier).Value = scope.TenantId;
            parameters.Add("@company", SqlDbType.UniqueIdentifier).Value = scope.CompanyId;
            parameters.Add("@binding", SqlDbType.UniqueIdentifier).Value = scope.SourceBindingId;
            parameters.Add("@batch", SqlDbType.UniqueIdentifier).Value = handle.Receipt.BatchId;
            parameters.Add("@planHash", SqlDbType.Char, 64).Value = plan.Sha256;
            parameters.Add("@claimOperation", SqlDbType.UniqueIdentifier).Value = handle.Receipt.OperationId;
            parameters.Add("@claimEpoch", SqlDbType.BigInt).Value = handle.Receipt.Epoch;
            parameters.Add("@created", SqlDbType.DateTimeOffset).Value = now;
        }
        async Task<int> CountAsync(string table, SqlTransaction? transaction)
        {
            if (table is not ("GroupAutomaticInputPlans" or "GroupOriginalAdmissionSidecars")) throw new InvalidOperationException();
            await using var command = new SqlCommand($"SELECT COUNT(*) FROM aioffice.{table} WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", connection, transaction) { CommandTimeout = 10 };
            command.Parameters.Add("@tenant", SqlDbType.UniqueIdentifier).Value = scope.TenantId;
            command.Parameters.Add("@company", SqlDbType.UniqueIdentifier).Value = scope.CompanyId;
            command.Parameters.Add("@binding", SqlDbType.UniqueIdentifier).Value = scope.SourceBindingId;
            return (int)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException());
        }
    }

    // Some invalid fields violate both a CHECK and a restrictive FK. SQL may
    // report either applicable constraint; no ordering is assumed. Distinct
    // sidecar-hash cases still exercise the Identity CHECK without an FK overlap.
    internal static bool IsExpectedConstraintFailure(string name, int number, string message)
    {
        if (number != 547) return false;
        const string plan = "FK_GroupOriginalAdmissionSidecars_OriginalPlan";
        const string claim = "FK_GroupOriginalAdmissionSidecars_OriginalClaim";
        const string identity = "CK_GroupOriginalAdmissionSidecars_Identity";
        string[] allowed = name switch
        {
            "missing-plan" or "wrong-plan-hash" => [plan],
            "wrong-claim-operation" or "wrong-claim-epoch" => [claim],
            "foreign-tenant" or "foreign-company" or "foreign-binding" => [plan, claim],
            "format-version" or "short-payload" or "oversized-payload" => ["CK_GroupOriginalAdmissionSidecars_Format"],
            "empty-operation" or "zero-epoch" or "negative-epoch" => [identity, claim],
            "lowercase-plan-hash" => [identity, plan],
            "nonhex-sidecar-hash" or "lowercase-sidecar-hash" => [identity],
            "non-UTC-time" => ["CK_GroupOriginalAdmissionSidecars_Time"],
            _ => []
        };
        return allowed.Any(constraint => message.Contains('"' + constraint + '"', StringComparison.Ordinal)
            || message.Contains('\'' + constraint + '\'', StringComparison.Ordinal));
    }
}
