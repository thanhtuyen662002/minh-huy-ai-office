using Microsoft.Data.SqlClient;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupOriginalAdmissionRuntimeProof
{
    internal static string FailureDiagnostic(string phase, Exception error)
    {
        var safe = phase is "setup" or "fresh-unsafe-permission-refusal" or "allocation" or "private-input" or "original"
            or "expiry-after-both-flushed" or "expiry-rewind-refusal" or "physical-constraint-boundaries" or "atomic-rollback"
            or "committed-reply-loss" or "fresh-read-replay" or "physical-column-boundaries" or "accepted-result" or "corrupted-sidecar-refusal"
            or "constraint-valid-pair" or "constraint-missing-plan" or "constraint-wrong-plan-hash"
            or "constraint-wrong-claim-operation" or "constraint-wrong-claim-epoch" or "constraint-foreign-tenant"
            or "constraint-foreign-company" or "constraint-foreign-binding" or "constraint-format-version" or "constraint-short-payload"
            or "constraint-oversized-payload" or "constraint-empty-operation" or "constraint-zero-epoch" or "constraint-negative-epoch"
            or "constraint-lowercase-plan-hash" or "constraint-nonhex-sidecar-hash" or "constraint-lowercase-sidecar-hash"
            or "constraint-non-UTC-time" or "sql-read-original" or "sql-read-predicates"
            or "admitted-journal-prepare" or "admitted-journal-rollback" or "admitted-journal-commit" or "admitted-journal-replay" ? phase : "unknown";
        var kind = error switch
        {
            SqlException { Number: 547 } => "sql-constraint",
            SqlException { Number: 229 } => "sql-permission",
            SqlException { Number: -2 } => "sql-timeout",
            SqlException { Number: 1205 } => "sql-deadlock",
            SqlException => "sql-other",
            OperationCanceledException => "cancelled",
            UnauthorizedAccessException => "denied",
            InvalidOperationException => "invariant",
            _ => "other"
        };
        return "FAIL owned original admission runtime phase-" + safe + " kind-" + kind;
    }
}
