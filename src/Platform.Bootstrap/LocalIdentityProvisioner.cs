using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MinhHuy.AIOffice.Platform.Bootstrap;

public sealed class LocalIdentityProvisioner(HttpClient http)
{
    public async Task<string> ProvisionAsync(BootstrapOptions options)
    {
        using var login = await http.PostAsync("realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "admin-cli",
                ["grant_type"] = "password",
                ["username"] = "bootstrap-admin",
                ["password"] = options.IdentityAdminPassword
            }));
        EnsureSuccess(login);
        var token = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            token.GetProperty("access_token").GetString());
        var realmUrl = $"admin/realms/{BootstrapOptions.Realm}";
        using var realm = await http.GetAsync(realmUrl);
        if (realm.StatusCode == HttpStatusCode.NotFound)
        {
            using var created = await http.PostAsJsonAsync("admin/realms", new
            {
                realm = BootstrapOptions.Realm,
                enabled = true,
                sslRequired = "none",
                registrationAllowed = false,
                resetPasswordAllowed = false,
                clients = new[] { new
                {
                    clientId = BootstrapOptions.Realm, enabled = true, publicClient = true,
                    directAccessGrantsEnabled = true, standardFlowEnabled = false,
                    protocol = "openid-connect",
                    protocolMappers = new object[]
                    {
                        new { name = "identity-provider", protocol = "openid-connect",
                            protocolMapper = "oidc-hardcoded-claim-mapper", config = new Dictionary<string, string>
                            { ["claim.name"] = "idp", ["claim.value"] = BootstrapOptions.Provider,
                              ["jsonType.label"] = "String", ["access.token.claim"] = "true" } },
                        new { name = "api-audience", protocol = "openid-connect",
                            protocolMapper = "oidc-audience-mapper", config = new Dictionary<string, string>
                            { ["included.custom.audience"] = BootstrapOptions.Realm,
                              ["access.token.claim"] = "true" } }
                    }
                } }
            });
            EnsureSuccess(created);
        }
        else EnsureSuccess(realm);

        var userUrl = $"{realmUrl}/users?username={BootstrapOptions.OwnerUsername}&exact=true";
        async Task<JsonElement[]> Users() => await http.GetFromJsonAsync<JsonElement[]>(userUrl)
            ?? throw new InvalidOperationException("Identity lookup returned no result.");
        var users = await Users();
        if (users.Length == 0)
        {
            using var created = await http.PostAsJsonAsync($"{realmUrl}/users", new
            {
                username = BootstrapOptions.OwnerUsername,
                enabled = true,
                email = "owner@aioffice.local",
                emailVerified = true,
                firstName = "Local",
                lastName = "Owner",
                credentials = new[] { new { type = "password", value = options.OwnerPassword, temporary = false } }
            });
            EnsureSuccess(created);
            users = await Users();
        }
        if (users.Length != 1 || users[0].GetProperty("username").GetString() != BootstrapOptions.OwnerUsername)
            throw new InvalidOperationException("Local owner identity is ambiguous.");
        return users[0].GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Local owner subject is absent.");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        // Do not log server bodies: an administrative request can contain credentials.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Identity provisioning HTTP {(int)response.StatusCode}.");
    }
}
