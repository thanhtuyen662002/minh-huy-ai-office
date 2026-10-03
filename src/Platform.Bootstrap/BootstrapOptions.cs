using System.Text.RegularExpressions;

namespace MinhHuy.AIOffice.Platform.Bootstrap;

public sealed record BootstrapOptions(
    Guid InstallationId, Guid TenantId, Guid CompanyId, Guid UserId, Guid DataSourceId,
    string SqlPassword, string RuntimePassword, string ReaderPassword,
    string IdentityAdminPassword, string OwnerPassword)
{
    public const string Provider = "local-keycloak";
    public const string Realm = "aioffice-local";
    public const string OwnerUsername = "owner";

    public static BootstrapOptions Read(Func<string, string?> environment)
    {
        if (environment("DOTNET_ENVIRONMENT") != "Development"
            || environment("AIOFFICE_LOCAL_BOOTSTRAP") != "true")
        {
            throw new InvalidOperationException("Bootstrap requires explicit local Development mode.");
        }

        Guid Id(string name) => Guid.TryParse(environment(name), out var id) && id != Guid.Empty
            ? id : throw new InvalidOperationException($"A valid {name} is required.");
        string Secret(string name) => environment(name) is { } secret
            && Regex.IsMatch(secret, "\\A[A-Za-z0-9_-]{32,128}\\z", RegexOptions.CultureInvariant)
                ? secret : throw new InvalidOperationException($"A valid generated {name} is required.");

        return new(Id("AIOFFICE_INSTALLATION_ID"), Id("AIOFFICE_TENANT_ID"),
            Id("AIOFFICE_COMPANY_ID"), Id("AIOFFICE_USER_ID"), Id("AIOFFICE_DATA_SOURCE_ID"),
            Secret("AIOFFICE_SQL_PASSWORD"), Secret("AIOFFICE_RUNTIME_PASSWORD"),
            Secret("AIOFFICE_READER_PASSWORD"), Secret("AIOFFICE_IDENTITY_ADMIN_PASSWORD"),
            Secret("AIOFFICE_OWNER_PASSWORD"));
    }
}
