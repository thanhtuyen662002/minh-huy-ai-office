using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

public static class OwnedGroupReferenceProofGuard
{
    public static void RequireKillableChild(int processId)
    {
        if (processId <= 1) throw new InvalidOperationException();
    }

    // Permission proof and current grants share the shipping authorization
    // refusal type. An unrelated operation/transport failure is not evidence.
    public static bool IsExpectedRefusal(string mode, Exception error) => mode switch
    {
        "deny" or "unsafe" or "allocation-deny" or "allocation-unsafe" or "claim-deny" or "claim-unsafe" => error is UnauthorizedAccessException,
        "rollback" => error is DbUpdateException,
        "allocation-rollback" => error is MinhHuy.AIOffice.Platform.Persistence.GroupBatchAllocationCommitException,
        "claim-rollback" => error is MinhHuy.AIOffice.Platform.Persistence.GroupBatchClaimCommitException,
        _ => false
    };

    public static void RequireOwned(Func<string, string?> environment)
    {
        if (environment("CI") != "true" || environment("GITHUB_ACTIONS") != "true" ||
            environment("AIOFFICE_OWNED_GROUP_REFERENCE_PROOF") != "true") throw new InvalidOperationException();
    }

    public static string RequireRuntimeConnection(string? connection)
    {
        var value = new SqlConnectionStringBuilder(connection);
        if (value.DataSource != "sql" || value.InitialCatalog != "AIOfficeLocal" || value.UserID != "aioffice_runtime" ||
            value.IntegratedSecurity || value.Authentication != SqlAuthenticationMethod.NotSpecified || value.Enlist == false ||
            value.AttachDBFilename.Length != 0 || value.FailoverPartner.Length != 0 || value.Password.Length is < 32 or > 128 ||
            value.Password.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new InvalidOperationException();
        value.ConnectTimeout = 10;
        return value.ConnectionString;
    }
}
