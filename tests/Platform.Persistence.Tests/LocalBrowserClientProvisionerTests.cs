extern alias Bootstrap;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using BootstrapOptions = Bootstrap::MinhHuy.AIOffice.Platform.Bootstrap.BootstrapOptions;
using LocalBrowserClientProvisioner = Bootstrap::MinhHuy.AIOffice.Platform.Bootstrap.LocalBrowserClientProvisioner;
using LocalIdentityProvisioner = Bootstrap::MinhHuy.AIOffice.Platform.Bootstrap.LocalIdentityProvisioner;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class LocalBrowserClientProvisionerTests
{
    private static BootstrapOptions Options() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "Aa1_abcdefghijklmnopqrstuvwxyz0123456789", "Aa1_abcdefghijklmnopqrstuvwxyz0123456789",
        "Aa1_abcdefghijklmnopqrstuvwxyz0123456789", "Aa1_abcdefghijklmnopqrstuvwxyz0123456789", "Aa1_abcdefghijklmnopqrstuvwxyz0123456789");

    [Fact]
    public async Task CreatesOnlySeparateOwnedPkceClientWithAccessOnlyApiAudience()
    {
        var options = Options() with { WebPort = 3300 };
        using var handler = new IdentityFixture(options);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity:8080/") };
        await new LocalBrowserClientProvisioner(http).EnsureAsync(options);
        var client = handler.Client!.Value;
        Assert.Equal(1, handler.Creates);
        Assert.Equal(BootstrapOptions.BrowserClientId, client.GetProperty("clientId").GetString());
        Assert.True(client.GetProperty("enabled").GetBoolean());
        Assert.True(client.GetProperty("publicClient").GetBoolean());
        Assert.True(client.GetProperty("standardFlowEnabled").GetBoolean());
        foreach (var property in new[] { "directAccessGrantsEnabled", "implicitFlowEnabled", "serviceAccountsEnabled", "authorizationServicesEnabled", "fullScopeAllowed" })
            Assert.False(client.GetProperty(property).GetBoolean());
        Assert.Equal(new[] { "http://127.0.0.1:3300/api/local/session/oidc/callback" },
            client.GetProperty("redirectUris").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(client.GetProperty("webOrigins").EnumerateArray());
        Assert.Equal(new[] { "basic", "profile", "email" },
            client.GetProperty("defaultClientScopes").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(client.GetProperty("optionalClientScopes").EnumerateArray());
        Assert.Equal("S256", client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
        Assert.Equal("RS256", client.GetProperty("attributes").GetProperty("id.token.signed.response.alg").GetString());
        Assert.Equal(options.InstallationId.ToString("D"), client.GetProperty("attributes").GetProperty(LocalBrowserClientProvisioner.InstallationAttribute).GetString());
        foreach (var mapper in client.GetProperty("protocolMappers").EnumerateArray())
        {
            var config = mapper.GetProperty("config");
            Assert.Equal("true", config.GetProperty("access.token.claim").GetString());
            Assert.Equal("false", config.GetProperty("id.token.claim").GetString());
        }
        Assert.DoesNotContain(options.OwnerPassword, client.GetRawText());
        Assert.All(handler.Paths, path => Assert.Contains("/clients", path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedOwnedClientReconcilesOnlyItsContractAndPreservesDisablement(bool enabled)
    {
        var options = Options();
        using var handler = new IdentityFixture(options) { Client = Existing(options, enabled) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity:8080/") };
        var provisioner = new LocalBrowserClientProvisioner(http);
        await provisioner.EnsureAsync(options);
        var first = handler.Client!.Value.GetRawText();
        await provisioner.EnsureAsync(options);
        Assert.Equal(first, handler.Client!.Value.GetRawText());
        Assert.Equal(enabled, handler.Client.Value.GetProperty("enabled").GetBoolean());
        Assert.Contains("basic", handler.Client.Value.GetProperty("defaultClientScopes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(0, handler.Creates);
        Assert.Equal(2, handler.Updates);
        Assert.All(handler.Paths, path => Assert.Contains("/clients", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OperatorDisableAfterDetailReadCannotBeOverwrittenByStaleSnapshot()
    {
        var options = Options();
        using var handler = new IdentityFixture(options) { Client = Existing(options, true), DisableBeforeUpdate = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity:8080/") };
        await new LocalBrowserClientProvisioner(http).EnsureAsync(options);
        Assert.Equal(1, handler.Updates);
        Assert.False(handler.Client!.Value.GetProperty("enabled").GetBoolean());
        Assert.False(handler.LastUpdate!.Value.TryGetProperty("enabled", out _));
        Assert.True(handler.Client.Value.GetProperty("standardFlowEnabled").GetBoolean());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("missing-owner")]
    [InlineData("wrong-name")]
    [InlineData("ambiguous")]
    public async Task RefusesForeignOrAmbiguousClientWithoutAnyUpdate(string mode)
    {
        var options = Options();
        using var handler = new IdentityFixture(options) { Client = Existing(options, true, mode), Ambiguous = mode == "ambiguous" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity:8080/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalBrowserClientProvisioner(http).EnsureAsync(options));
        Assert.Equal(0, handler.Creates);
        Assert.Equal(0, handler.Updates);
    }

    [Fact]
    public async Task ConcurrentCreationConflictRechecksOwnership()
    {
        var options = Options();
        using var handler = new IdentityFixture(options) { Conflict = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity:8080/") };
        await new LocalBrowserClientProvisioner(http).EnsureAsync(options);
        Assert.Equal(1, handler.Creates);
        Assert.Equal(1, handler.Updates);
    }

    [Fact]
    public async Task ForeignCreationConflictCannotBecomeClientTakeover()
    {
        var options = Options();
        using var handler = new IdentityFixture(options) { Conflict = true, ForeignConflict = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity:8080/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalBrowserClientProvisioner(http).EnsureAsync(options));
        Assert.Equal(1, handler.Creates);
        Assert.Equal(0, handler.Updates);
    }

    [Fact]
    public async Task RetainedRealmAddsBrowserClientWithoutChangingExistingOwnerOrPilotClient()
    {
        var options = Options();
        using var handler = new RetainedIdentityFixture(options);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity:8080/") };
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", await new LocalIdentityProvisioner(http).ProvisionAsync(options));
        Assert.Equal(1, handler.OwnerReads);
        Assert.Equal(1, handler.Browser.Creates);
        Assert.All(handler.Browser.Paths, path => Assert.Contains("/clients", path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("03000")]
    [InlineData("3000\n")]
    [InlineData("3000/foreign")]
    public void RefusesInvalidBrowserPort(string port)
    {
        Assert.Throws<InvalidOperationException>(() => BootstrapOptions.Read(name => name switch
        {
            "DOTNET_ENVIRONMENT" => "Development",
            "AIOFFICE_LOCAL_BOOTSTRAP" => "true",
            "AIOFFICE_WEB_PORT" => port,
            _ when name.EndsWith("_ID", StringComparison.Ordinal) => Guid.NewGuid().ToString(),
            _ => "Aa1_abcdefghijklmnopqrstuvwxyz0123456789"
        }));
    }

    private static JsonElement Existing(BootstrapOptions options, bool enabled, string mode = "owned") => JsonSerializer.SerializeToElement(new
    {
        id = IdentityFixture.ClientId,
        clientId = mode == "wrong-name" ? "foreign-client" : BootstrapOptions.BrowserClientId,
        enabled,
        attributes = mode == "missing-owner" ? new Dictionary<string, string>() : new Dictionary<string, string>
        {
            [LocalBrowserClientProvisioner.InstallationAttribute] = mode == "foreign" ? Guid.NewGuid().ToString("D") : options.InstallationId.ToString("D")
        },
        redirectUris = new[] { "http://untrusted.invalid/*" }
    });

    private sealed class IdentityFixture(BootstrapOptions options) : HttpMessageHandler
    {
        public const string ClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        public JsonElement? Client { get; set; }
        public bool Ambiguous { get; init; }
        public bool Conflict { get; init; }
        public bool ForeignConflict { get; init; }
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public bool DisableBeforeUpdate { get; init; }
        public JsonElement? LastUpdate { get; private set; }
        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Assert.StartsWith($"/admin/realms/{BootstrapOptions.Realm}/clients", path, StringComparison.Ordinal);
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith(ClientId, StringComparison.Ordinal)) return Response(Client);
                if (Client is null) return Response(Array.Empty<JsonElement>());
                return Response(Ambiguous ? new[] { Client.Value, Client.Value } : new[] { Client.Value });
            }
            if (request.Method == HttpMethod.Post)
            {
                Creates++;
                Client = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (Conflict) Client = Existing(options, true, ForeignConflict ? "foreign" : "owned");
                return new HttpResponseMessage(Conflict ? HttpStatusCode.Conflict : HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Put)
            {
                Updates++;
                LastUpdate = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                Assert.False(LastUpdate.Value.TryGetProperty("enabled", out _));
                // Model Keycloak's updateClient rule: absent enabled retains
                // the live model value, including a disable after detail GET.
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(LastUpdate.Value.GetRawText())!;
                fields["enabled"] = JsonSerializer.SerializeToElement(!DisableBeforeUpdate && Client!.Value.GetProperty("enabled").GetBoolean());
                Client = JsonSerializer.SerializeToElement(fields);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected identity mutation.");
        }

        private static HttpResponseMessage Response<T>(T value)
        {
            // Keycloak assigns the stable UUID; it is not part of create/PUT input.
            if (value is JsonElement element && !element.TryGetProperty("id", out _))
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(element.GetRawText())!;
                fields["id"] = JsonSerializer.SerializeToElement(ClientId);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(fields) };
            }
            if (value is JsonElement[] elements && elements.Length == 1 && !elements[0].TryGetProperty("id", out _))
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(elements[0].GetRawText())!;
                fields["id"] = JsonSerializer.SerializeToElement(ClientId);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { fields }) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        }
    }

    private sealed class RetainedIdentityFixture(BootstrapOptions options) : HttpMessageHandler
    {
        public IdentityFixture Browser { get; } = new(options);
        public int OwnerReads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/realms/master/protocol/openid-connect/token" && request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "fixture-admin-token" }) };
            if (path == $"/admin/realms/{BootstrapOptions.Realm}" && request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { realm = BootstrapOptions.Realm }) };
            if (path == $"/admin/realms/{BootstrapOptions.Realm}/users" && request.Method == HttpMethod.Get)
            {
                OwnerReads++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new
                {
                    id = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", username = BootstrapOptions.OwnerUsername,
                    enabled = false, credentials = new[] { new { type = "password", value = "retained-private-password" } }
                } })
                };
            }
            Assert.StartsWith($"/admin/realms/{BootstrapOptions.Realm}/clients", path, StringComparison.Ordinal);
            using var invoker = new HttpMessageInvoker(Browser, disposeHandler: false);
            return await invoker.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Browser.Dispose();
            base.Dispose(disposing);
        }
    }
}
