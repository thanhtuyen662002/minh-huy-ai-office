using System.Net;
using System.Text.Json;
using MinhHuy.AIOffice.Core.Api;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class KeycloakAdminClientTests
{
    [Fact]
    public async Task Uses_client_credentials_exact_email_and_temporary_credential()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-access-token", request.Headers.Authorization.Parameter);
            Assert.True(request.Headers.CacheControl!.NoStore);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("?exact=true&max=2&email=member%2Btest%40example.com", request.RequestUri!.Query);
                return Json("[]");
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.True(root.GetProperty("emailVerified").GetBoolean());
            Assert.Equal("member+test@example.com", root.GetProperty("email").GetString());
            Assert.Equal("member+test@example.com", root.GetProperty("username").GetString());
            Assert.Equal("UPDATE_PASSWORD", Assert.Single(root.GetProperty("requiredActions").EnumerateArray()).GetString());
            var credential = Assert.Single(root.GetProperty("credentials").EnumerateArray());
            Assert.Equal("password", credential.GetProperty("type").GetString());
            Assert.Equal("SyntheticTempPassword123!", credential.GetProperty("value").GetString());
            Assert.True(credential.GetProperty("temporary").GetBoolean());
            return new(HttpStatusCode.Created);
        };
        Assert.False(await fixture.Client.UserExistsAsync("member+test@example.com", default));
        Assert.True(await fixture.Client.CreateUserAsync("member+test@example.com", "SyntheticTempPassword123!", default));
        Assert.Equal(2, fixture.Handler.TokenRequests);
    }

    [Theory]
    [InlineData("[{\"email\":\"MEMBER@example.com\"}]", true)]
    [InlineData("[]", false)]
    public async Task Exact_lookup_allows_only_matching_email(string body, bool exists)
    {
        using var fixture = new Fixture(); fixture.Handler.Reply = _ => Task.FromResult(Json(body));
        Assert.Equal(exists, await fixture.Client.UserExistsAsync("member@example.com", default));
    }

    [Theory]
    [InlineData("[{\"email\":\"other@example.com\"}]")]
    [InlineData("[{\"email\":\"member@example.com\"},{\"email\":\"member@example.com\"}]")]
    [InlineData("{\"private\":\"SyntheticTempPassword123!\"}")]
    [InlineData("invalid-private-body")]
    public async Task Invalid_upstream_body_does_not_escape_in_exception(string body)
    {
        using var fixture = new Fixture(); fixture.Handler.Reply = _ => Task.FromResult(Json(body));
        var error = await Assert.ThrowsAsync<KeycloakProvisioningException>(() => fixture.Client.UserExistsAsync("member@example.com", default));
        Assert.DoesNotContain(body, error.ToString()); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Conflict_race_queries_existing_user_without_reset()
    {
        using var fixture = new Fixture(); var methods = new List<HttpMethod>();
        fixture.Handler.Reply = request =>
        {
            methods.Add(request.Method);
            return Task.FromResult(request.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.Conflict)
                : Json("[{\"email\":\"member@example.com\"}]"));
        };
        Assert.False(await fixture.Client.CreateUserAsync("member@example.com", "SyntheticTempPassword123!", default));
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, methods);
    }

    [Theory]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(500, false)]
    [InlineData(503, true)]
    [InlineData(429, true)]
    public async Task Upstream_errors_are_sanitized(int status, bool unavailable)
    {
        using var fixture = new Fixture(); fixture.Handler.Reply = _ => Task.FromResult(Json("PRIVATE-CREDENTIAL", (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<KeycloakProvisioningException>(() => fixture.Client.CreateUserAsync("member@example.com", "SyntheticTempPassword123!", default));
        Assert.Equal(unavailable, error.Unavailable); Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public async Task Timeout_and_disabled_client_do_not_leak_errors()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = _ => throw new TaskCanceledException("PRIVATE-CREDENTIAL");
        var error = await Assert.ThrowsAsync<KeycloakProvisioningException>(() => fixture.Client.UserExistsAsync("member@example.com", default));
        Assert.True(error.Unavailable); Assert.DoesNotContain("PRIVATE", error.ToString());
        fixture.Options.Enabled = false;
        await Assert.ThrowsAsync<KeycloakProvisioningException>(() => fixture.Client.UserExistsAsync("member@example.com", default));
        Assert.Equal(1, fixture.Handler.TokenRequests);
    }

    [Theory]
    [InlineData("http://identity.example.test")]
    [InlineData("https://identity.example.test/path")]
    [InlineData("https://user:secret@identity.example.test")]
    public void Invalid_configuration_is_rejected(string origin)
    {
        var options = new KeycloakProvisioningOptions { Enabled = true, BaseUrl = origin, ClientSecretRef = "secretref://env/TEST_ONLY" };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.Enabled = false; options.Validate();
    }

    [Fact]
    public async Task Secret_file_is_read_only_at_request_time_and_missing_file_is_sanitized()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(Path.GetTempPath(), $"keycloak307-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "synthetic-private-client-secret\n");
            fixture.Options.ClientSecretRef = ""; fixture.Options.ClientSecretFile = path;
            fixture.Options.Validate();
            Assert.False(await fixture.Client.UserExistsAsync("member@example.com", default));
            File.Delete(path);
            var error = await Assert.ThrowsAsync<KeycloakProvisioningException>(() => fixture.Client.UserExistsAsync("member@example.com", default));
            Assert.True(error.Unavailable); Assert.DoesNotContain(path, error.ToString()); Assert.Null(error.InnerException);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("PRIVATE-MALFORMED-TOKEN")]
    [InlineData("{\"access_token\":null}")]
    [InlineData("{}")]
    public async Task Token_failures_are_sanitized_without_calling_admin_api(string body)
    {
        using var fixture = new Fixture(); fixture.Handler.TokenBody = body;
        fixture.Handler.Reply = _ => throw new InvalidOperationException("Admin API must not be called.");
        var error = await Assert.ThrowsAsync<KeycloakProvisioningException>(() => fixture.Client.UserExistsAsync("member@example.com", default));
        Assert.DoesNotContain(body, error.ToString()); Assert.Null(error.InnerException);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
    private sealed class Fixture : IDisposable
    {
        private readonly string variable = "KEYCLOAK307_" + Guid.NewGuid().ToString("N");
        private readonly HttpClient http;
        public Handler Handler { get; } = new();
        public KeycloakProvisioningOptions Options { get; }
        public KeycloakAdminClient Client { get; }
        public Fixture()
        {
            Environment.SetEnvironmentVariable(variable, "synthetic-private-client-secret");
            Options = new() { Enabled = true, BaseUrl = "https://identity.example.test", ClientSecretRef = "secretref://env/" + variable };
            Options.Validate(); http = new(Handler); Client = new(http, Options);
        }
        public void Dispose() { http.Dispose(); Environment.SetEnvironmentVariable(variable, null); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Reply { get; set; } = _ => Task.FromResult(Json("[]"));
        public int TokenRequests { get; private set; }
        public string TokenBody { get; set; } = "{\"access_token\":\"synthetic-access-token\"}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                TokenRequests++;
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("grant_type=client_credentials", form);
                Assert.Contains("client_id=aioffice-member-provisioner", form);
                Assert.Contains("client_secret=synthetic-private-client-secret", form);
                return Json(TokenBody);
            }
            return await Reply(request);
        }
    }
}
