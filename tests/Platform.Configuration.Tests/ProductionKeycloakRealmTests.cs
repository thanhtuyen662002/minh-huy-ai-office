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
        Assert.False(realm.TryGetProperty("users", out _));
        var client = Assert.Single(realm.GetProperty("clients").EnumerateArray());
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

    private static JsonDocument ReadRealm()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "infra", "keycloak", "aioffice-realm.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllText(path));
        }
        throw new InvalidOperationException("Could not locate production realm.");
    }
}
