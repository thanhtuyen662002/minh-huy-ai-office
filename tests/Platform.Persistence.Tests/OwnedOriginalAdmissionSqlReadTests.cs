extern alias GroupReferenceProof;

using System.Data;
using Microsoft.Data.SqlClient;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalAdmissionRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedOriginalAdmissionSqlReadTests
{
    private static readonly GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Batch = Guid.NewGuid();

    private static SqlCommand Command()
    {
        var command = new SqlCommand("SELECT TOP(2) [s].[TenantId] FROM (SELECT TenantId,CompanyId,BindingId,BatchId,FormatVersion,PlanSha256,SidecarSha256,"
            + "OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc,CASE WHEN DATALENGTH([Sidecar]) BETWEEN 211 AND 1010000 THEN [Sidecar] END AS [Sidecar] FROM aioffice.GroupOriginalAdmissionSidecars) AS [s] "
            + "WHERE [s].[TenantId] = @tenant AND [s].[CompanyId] = @company AND [s].[BindingId] = @binding AND [s].[BatchId] = @batch");
        command.Parameters.Add("@tenant", SqlDbType.UniqueIdentifier).Value = Scope.TenantId;
        command.Parameters.Add("@company", SqlDbType.UniqueIdentifier).Value = Scope.CompanyId;
        command.Parameters.Add("@binding", SqlDbType.UniqueIdentifier).Value = Scope.SourceBindingId;
        command.Parameters.Add("@batch", SqlDbType.UniqueIdentifier).Value = Batch;
        return command;
    }

    [Fact]
    public void TypedOriginalSelectorsAreBoundWithoutChangingAnyParameter()
    {
        using var command = Command();
        var parameters = command.Parameters.Cast<SqlParameter>().ToArray();
        var values = parameters.Select(p => p.Value).ToArray(); var text = command.CommandText;
        var bound = Proof.ReadSelectorParameters(command, Scope, Batch);
        Assert.Equal(4, bound.Count); Assert.Same(parameters[0], bound["TenantId"]); Assert.Same(parameters[3], bound["BatchId"]);
        Assert.Equal(text, command.CommandText); Assert.Equal(values, parameters.Select(p => p.Value).ToArray());
        // Shape/typing only; this command is not executed and grants no SQL predicate evidence.
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("type")]
    [InlineData("direction")]
    [InlineData("duplicate-parameter")]
    [InlineData("missing-predicate")]
    [InlineData("or-predicate")]
    [InlineData("alias")]
    [InlineData("not-bounded")]
    [InlineData("extra-parameter")]
    [InlineData("wrong-table")]
    [InlineData("command-type")]
    [InlineData("empty-identity")]
    [InlineData("extra-statement")]
    [InlineData("unbounded-payload")]
    public void UntrustedOrDifferentCommandShapeRefusesBeforeAnyCloneExecution(string change)
    {
        using var command = Command();
        switch (change)
        {
            case "scope": command.Parameters["@tenant"].Value = Guid.NewGuid(); break;
            case "type": command.Parameters["@tenant"].SqlDbType = SqlDbType.NVarChar; break;
            case "direction": command.Parameters["@tenant"].Direction = ParameterDirection.InputOutput; break;
            case "duplicate-parameter": command.CommandText = command.CommandText.Replace("= @company", "= @tenant", StringComparison.Ordinal); break;
            case "missing-predicate": command.CommandText = command.CommandText.Replace(" AND [s].[BatchId] = @batch", "", StringComparison.Ordinal); break;
            case "or-predicate": command.CommandText = command.CommandText.Replace(" AND ", " OR ", StringComparison.Ordinal); break;
            case "alias": command.CommandText = command.CommandText.Replace("[s].[CompanyId]", "[foreign].[CompanyId]", StringComparison.Ordinal); break;
            case "not-bounded": command.CommandText = command.CommandText.Replace("TOP(2)", "TOP(3)", StringComparison.Ordinal); break;
            case "extra-parameter": command.Parameters.Add("@extra", SqlDbType.Int).Value = 1; break;
            case "wrong-table": command.CommandText = command.CommandText.Replace("GroupOriginalAdmissionSidecars", "GroupAutomaticInputPlans", StringComparison.Ordinal); break;
            case "command-type": command.CommandType = CommandType.StoredProcedure; break;
            case "extra-statement":
                command.CommandText = command.CommandText.Replace(
                "FROM aioffice.GroupOriginalAdmissionSidecars)", "FROM aioffice.GroupOriginalAdmissionSidecars; DELETE FROM aioffice.GroupOriginalAdmissionSidecars)", StringComparison.Ordinal); break;
            case "unbounded-payload": command.CommandText = command.CommandText.Replace("BETWEEN 211 AND 1010000", "BETWEEN 0 AND 2147483647", StringComparison.Ordinal); break;
            default: command.Parameters["@batch"].Value = Guid.Empty; break;
        }
        Assert.Throws<InvalidOperationException>(() => Proof.ReadSelectorParameters(command, Scope, Batch));
    }
}
