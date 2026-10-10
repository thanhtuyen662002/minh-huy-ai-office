using Microsoft.Data.SqlClient;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

public static class OwnedGroupReferenceProofGuard
{
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
