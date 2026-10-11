using System.Data;
using Microsoft.Data.SqlClient;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static class GroupCursorLegacyProbes
{
    internal const string Predecessor = "20261011061648_AddGroupTerminalKeyEnrollment";
    internal const string Target = "20261011075456_AddGroupSignedTerminalCursor";
    internal const string PreflightMessage = "Existing terminal cursor requires reviewed signed history migration.";

    // These all satisfy the legacy value CHECK and scoped terminal FK. They
    // are migration refusals only if real SQL returns exactly the new 51048.
    internal static SqlCommand[] MigrationRefusals(SqlConnection shadow, GroupScope scope) =>
    [
        CursorUpdate(shadow, scope, "[ThroughSequence]=0,[LastTerminalBatchId]=NULL"),
        CursorUpdate(shadow, scope, "[Version]=[Version]+1"),
        CursorUpdate(shadow, scope, "[ThroughSequence]=[ThroughSequence]+1"),
        CursorUpdate(shadow, scope, "[UpdatedAtUtc]=DATEADD(nanosecond,100,[UpdatedAtUtc])")
    ];

    // Rejection here proves the retained old CHECK, not migration preflight.
    internal static SqlCommand[] LegacyConstraintRefusals(SqlConnection shadow, GroupScope scope) =>
    [
        CursorUpdate(shadow, scope, "[Version]=0"),
        CursorUpdate(shadow, scope, "[UpdatedAtUtc]=SWITCHOFFSET([UpdatedAtUtc],'+01:00')")
    ];

    internal static SqlCommand RestoreMatchingCursor(SqlConnection shadow, GroupScope scope)
    {
        scope.Validate();
        GroupCursorShadowGuard.RequireShadowConnection(shadow);
        var command = new SqlCommand("""
            UPDATE c SET [ThroughSequence]=a.[ThroughSequence],[Version]=a.[Version],
                [LastTerminalBatchId]=a.[BatchId],[UpdatedAtUtc]=a.[AdvancedAtUtc]
            FROM [aioffice].[GroupTerminalFrontierStates] c
            JOIN [aioffice].[GroupTerminalAdvances] a
                ON a.[TenantId]=c.[TenantId] AND a.[CompanyId]=c.[CompanyId] AND a.[BindingId]=c.[BindingId] AND a.[Version]=1
            WHERE c.[TenantId]=@tenant AND c.[CompanyId]=@company AND c.[BindingId]=@binding;
            SELECT @@ROWCOUNT;
            """, shadow) { CommandTimeout = 30 };
        BindScope(command, scope); return command;
    }

    internal static SqlCommand RemoveAdvance(SqlConnection shadow, GroupScope scope)
    {
        scope.Validate();
        GroupCursorShadowGuard.RequireShadowConnection(shadow);
        var command = new SqlCommand("""
            DELETE FROM [aioffice].[GroupTerminalAdvances]
            WHERE [TenantId]=@tenant AND [CompanyId]=@company AND [BindingId]=@binding AND [Version]=1;
            SELECT @@ROWCOUNT;
            """, shadow) { CommandTimeout = 30 };
        BindScope(command, scope); return command;
    }

    private static SqlCommand CursorUpdate(SqlConnection shadow, GroupScope scope, string assignment)
    {
        scope.Validate();
        GroupCursorShadowGuard.RequireShadowConnection(shadow);
        // Assignment is private and comes only from the closed arrays above.
        // Each actual mutation owns a fresh unopened connection. SqlClient
        // hides Password after Open, so a previously opened template cannot
        // pass the strict normalized guard again.
        var command = new SqlCommand($"""
            UPDATE [aioffice].[GroupTerminalFrontierStates] SET {assignment}
            WHERE [TenantId]=@tenant AND [CompanyId]=@company AND [BindingId]=@binding;
            SELECT @@ROWCOUNT;
            """, new SqlConnection(shadow.ConnectionString)) { CommandTimeout = 30 };
        BindScope(command, scope); return command;
    }

    private static void BindScope(SqlCommand command, GroupScope scope)
    {
        command.Parameters.Add("@tenant", SqlDbType.UniqueIdentifier).Value = scope.TenantId;
        command.Parameters.Add("@company", SqlDbType.UniqueIdentifier).Value = scope.CompanyId;
        command.Parameters.Add("@binding", SqlDbType.UniqueIdentifier).Value = scope.SourceBindingId;
    }
}
