using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MinhHuy.AIOffice.Platform.Bootstrap;

// This client is separate from the retained pilot client and owner identity.
// Reconciliation may touch only the exact client owned by this installation.
public sealed class LocalBrowserClientProvisioner(HttpClient http)
{
    public const string InstallationAttribute = "aioffice.installation-id";

    public async Task EnsureAsync(BootstrapOptions options)
    {
        var clientsUrl = $"admin/realms/{BootstrapOptions.Realm}/clients";
        async Task<JsonElement[]> Clients() => await http.GetFromJsonAsync<JsonElement[]>(
            $"{clientsUrl}?clientId={BootstrapOptions.BrowserClientId}&search=false")
            ?? throw Refused();
        var clients = await Clients();
        if (clients.Length == 0)
        {
            using var created = await http.PostAsJsonAsync(clientsUrl, Desired(options, true));
            // A concurrent initial bootstrap can win creation. Re-read and
            // validate exact ownership; never convert a conflict into a takeover.
            if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.Conflict) throw Refused();
            clients = await Clients();
        }
        if (clients.Length != 1 || !clients[0].TryGetProperty("clientId", out var name)
            || name.GetString() != BootstrapOptions.BrowserClientId
            || !clients[0].TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out var clientId)
            || clientId == Guid.Empty) throw Refused();
        var clientUrl = $"{clientsUrl}/{clientId:D}";
        var client = await http.GetFromJsonAsync<JsonElement>(clientUrl);
        if (!client.TryGetProperty("id", out var detailId) || !Guid.TryParse(detailId.GetString(), out var verifiedId) || verifiedId != clientId
            || !client.TryGetProperty("clientId", out name) || name.GetString() != BootstrapOptions.BrowserClientId
            || !client.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty(InstallationAttribute, out var installation)
            || installation.ValueKind != JsonValueKind.String || installation.GetString() != options.InstallationId.ToString("D")
            || !client.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Refused();
        // Preserve deliberate client disablement. Owner subject/password,
        // realm policy, pilot client and company grants are never updated here.
        using var updated = await http.PutAsJsonAsync(clientUrl, Desired(options, enabled.GetBoolean()));
        if (!updated.IsSuccessStatusCode) throw Refused();
    }

    private static object Desired(BootstrapOptions options, bool enabled) => new
    {
        clientId = BootstrapOptions.BrowserClientId,
        enabled,
        protocol = "openid-connect",
        publicClient = true,
        standardFlowEnabled = true,
        directAccessGrantsEnabled = false,
        implicitFlowEnabled = false,
        serviceAccountsEnabled = false,
        authorizationServicesEnabled = false,
        fullScopeAllowed = false,
        consentRequired = false,
        redirectUris = new[] { options.BrowserRedirectUri },
        webOrigins = Array.Empty<string>(),
        defaultClientScopes = new[] { "profile", "email" },
        optionalClientScopes = Array.Empty<string>(),
        attributes = new Dictionary<string, string>
        {
            [InstallationAttribute] = options.InstallationId.ToString("D"),
            ["pkce.code.challenge.method"] = "S256",
            ["id.token.signed.response.alg"] = "RS256",
            ["access.token.signed.response.alg"] = "RS256"
        },
        protocolMappers = new object[]
        {
            new { name = "identity-provider", protocol = "openid-connect", protocolMapper = "oidc-hardcoded-claim-mapper",
                config = new Dictionary<string, string>
                {
                    ["claim.name"] = "idp", ["claim.value"] = BootstrapOptions.Provider,
                    ["jsonType.label"] = "String", ["access.token.claim"] = "true",
                    ["id.token.claim"] = "false", ["userinfo.token.claim"] = "false"
                } },
            new { name = "api-audience", protocol = "openid-connect", protocolMapper = "oidc-audience-mapper",
                config = new Dictionary<string, string>
                {
                    ["included.custom.audience"] = BootstrapOptions.Realm,
                    ["access.token.claim"] = "true", ["id.token.claim"] = "false"
                } }
        }
    };

    private static InvalidOperationException Refused() => new("Local browser identity client could not be provisioned safely.");
}
