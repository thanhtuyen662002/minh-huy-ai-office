using System.Text.Json;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Configuration.Tests;

public sealed class ProductionKeycloakRealmTests
{
    [Fact]
    public void Realm_preserves_browser_and_api_token_boundaries()
    {
        using var document = ReadRealm();
        var realm = document.RootElement;
        Assert.Equal("aioffice", realm.GetProperty("realm").GetString());
        Assert.Equal("all", realm.GetProperty("sslRequired").GetString());
        Assert.All(realm.GetProperty("users").EnumerateArray(), user => Assert.True(user.TryGetProperty("serviceAccountClientId", out _)));
        var client = Assert.Single(realm.GetProperty("clients").EnumerateArray(), item => item.GetProperty("publicClient").GetBoolean());
        Assert.Equal("${AIOFFICE_BROWSER_OIDC_CLIENT_ID}", client.GetProperty("clientId").GetString());
        Assert.True(client.GetProperty("publicClient").GetBoolean());
        Assert.True(client.GetProperty("standardFlowEnabled").GetBoolean());
        foreach (var name in new[] { "directAccessGrantsEnabled", "implicitFlowEnabled", "serviceAccountsEnabled",
                     "authorizationServicesEnabled", "fullScopeAllowed" })
            Assert.False(client.GetProperty(name).GetBoolean());
        Assert.Equal("${AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN}/api/local/session/oidc/callback",
            Assert.Single(client.GetProperty("redirectUris").EnumerateArray()).GetString());
        Assert.Empty(client.GetProperty("webOrigins").EnumerateArray());
        Assert.Empty(client.GetProperty("optionalClientScopes").EnumerateArray());
        Assert.Equal(new[] { "basic", "profile", "email" },
            client.GetProperty("defaultClientScopes").EnumerateArray().Select(scope => scope.GetString()));
        Assert.Equal("S256", client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
        Assert.Equal("RS256", client.GetProperty("attributes").GetProperty("id.token.signed.response.alg").GetString());
        var mappers = client.GetProperty("protocolMappers").EnumerateArray().ToArray();
        var audience = Assert.Single(mappers, mapper => mapper.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper");
        Assert.Equal("${AIOFFICE_AUDIENCE}", audience.GetProperty("config").GetProperty("included.custom.audience").GetString());
        var provider = Assert.Single(mappers, mapper => mapper.GetProperty("protocolMapper").GetString() == "oidc-hardcoded-claim-mapper");
        Assert.Equal("idp", provider.GetProperty("config").GetProperty("claim.name").GetString());
        Assert.Equal("company-keycloak", provider.GetProperty("config").GetProperty("claim.value").GetString());
        foreach (var mapper in mappers)
        {
            Assert.Equal("true", mapper.GetProperty("config").GetProperty("access.token.claim").GetString());
            Assert.Equal("false", mapper.GetProperty("config").GetProperty("id.token.claim").GetString());
        }
        // The standard email scope reports actual verification. Never stamp email_verified=true.
        Assert.DoesNotContain(mappers, mapper => mapper.GetProperty("config").TryGetProperty("claim.name", out var claim)
            && claim.GetString() == "email_verified");
    }

    [Fact]
    public void Realm_requires_initial_password_change_and_allows_optional_otp()
    {
        using var document = ReadRealm();
        var realm = document.RootElement;
        Assert.False(realm.GetProperty("registrationAllowed").GetBoolean());
        Assert.False(realm.GetProperty("duplicateEmailsAllowed").GetBoolean());
        Assert.True(realm.GetProperty("bruteForceProtected").GetBoolean());
        Assert.InRange(realm.GetProperty("failureFactor").GetInt32(), 1, 5);
        Assert.Contains("length(14)", realm.GetProperty("passwordPolicy").GetString(), StringComparison.Ordinal);
        Assert.Contains("passwordHistory(5)", realm.GetProperty("passwordPolicy").GetString(), StringComparison.Ordinal);
        var actions = realm.GetProperty("requiredActions").EnumerateArray().ToArray();
        var password = Assert.Single(actions, action => action.GetProperty("alias").GetString() == "UPDATE_PASSWORD");
        Assert.True(password.GetProperty("enabled").GetBoolean());
        Assert.True(password.GetProperty("defaultAction").GetBoolean());
        var otp = Assert.Single(actions, action => action.GetProperty("alias").GetString() == "CONFIGURE_TOTP");
        Assert.True(otp.GetProperty("enabled").GetBoolean());
        Assert.False(otp.GetProperty("defaultAction").GetBoolean());
    }

    [Fact]
    public void Provisioner_has_only_manage_users_in_both_service_account_and_token_scope()
    {
        using var document = ReadRealm();
        var realm = document.RootElement;
        Assert.Equal(2, realm.GetProperty("clients").GetArrayLength());
        var client = Assert.Single(realm.GetProperty("clients").EnumerateArray(), item => item.GetProperty("clientId").GetString() == "aioffice-member-provisioner");
        Assert.Equal("${AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED}", client.GetProperty("enabled").GetString());
        Assert.Equal("${AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET}", client.GetProperty("secret").GetString());
        Assert.True(client.GetProperty("serviceAccountsEnabled").GetBoolean());
        foreach (var name in new[] { "publicClient", "standardFlowEnabled", "implicitFlowEnabled", "directAccessGrantsEnabled", "fullScopeAllowed", "authorizationServicesEnabled" })
            Assert.False(client.GetProperty(name).GetBoolean());
        foreach (var name in new[] { "defaultClientScopes", "optionalClientScopes", "redirectUris", "webOrigins" })
            Assert.Empty(client.GetProperty(name).EnumerateArray());
        var account = Assert.Single(realm.GetProperty("users").EnumerateArray());
        Assert.Equal("aioffice-member-provisioner", account.GetProperty("serviceAccountClientId").GetString());
        Assert.False(account.TryGetProperty("credentials", out _));
        Assert.Empty(account.GetProperty("realmRoles").EnumerateArray());
        var roles = Assert.Single(account.GetProperty("clientRoles").EnumerateObject());
        Assert.Equal("realm-management", roles.Name);
        Assert.Equal("manage-users", Assert.Single(roles.Value.EnumerateArray()).GetString());
        var scopes = Assert.Single(realm.GetProperty("clientScopeMappings").EnumerateObject());
        Assert.Equal("realm-management", scopes.Name);
        var mapping = Assert.Single(scopes.Value.EnumerateArray());
        Assert.Equal("aioffice-member-provisioner", mapping.GetProperty("client").GetString());
        Assert.Equal("manage-users", Assert.Single(mapping.GetProperty("roles").EnumerateArray()).GetString());
        var mappers = client.GetProperty("protocolMappers").EnumerateArray().ToArray();
        Assert.Equal(3, mappers.Length);
        var subject = Assert.Single(mappers, item => item.GetProperty("protocolMapper").GetString() == "oidc-sub-mapper");
        Assert.Equal("true", subject.GetProperty("config").GetProperty("access.token.claim").GetString());
        var audience = Assert.Single(mappers, item => item.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper");
        Assert.Equal("realm-management", audience.GetProperty("config").GetProperty("included.client.audience").GetString());
        var mapper = Assert.Single(mappers, item => item.GetProperty("protocolMapper").GetString() == "oidc-usermodel-client-role-mapper");
        Assert.Equal("realm-management", mapper.GetProperty("config").GetProperty("usermodel.clientRoleMapping.clientId").GetString());
        Assert.Equal("resource_access.realm-management.roles", mapper.GetProperty("config").GetProperty("claim.name").GetString());
    }

    [Fact]
    public void Production_wiring_keeps_secret_out_of_web_and_worker_and_defaults_to_disabled()
    {
        var root = FindRoot();
        var production = File.ReadAllText(Path.Combine(root, "compose.production.yaml"));
        var identity = File.ReadAllText(Path.Combine(root, "compose.identity.yaml"));
        var template = File.ReadAllText(Path.Combine(root, ".env.production.example"));
        var start = File.ReadAllText(Path.Combine(root, "infra", "keycloak", "start.sh"));
        Assert.Contains("AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED=false", template);
        Assert.Contains("AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE=", template);
        foreach (var compose in new[] { production, identity })
        {
            Assert.Contains("${AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED:-false}", compose);
            Assert.Contains("file: ${AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE:-/dev/null}", compose);
            Assert.DoesNotContain("AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET:", compose);
        }
        Assert.Contains("/run/secrets/keycloak_provisioning_client_secret", start);
        Assert.Contains("export AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET=", start);
        Assert.DoesNotContain("keycloak_provisioning", production.Split("  core-api:")[0]);
        Assert.DoesNotContain("keycloak_provisioning", production.Split("  agent-worker:")[1].Split("\nsecrets:")[0]);
    }

    private static JsonDocument ReadRealm() => JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRoot(), "infra", "keycloak", "aioffice-realm.json")));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "infra", "keycloak", "aioffice-realm.json");
            if (File.Exists(path)) return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate production realm.");
    }
}
