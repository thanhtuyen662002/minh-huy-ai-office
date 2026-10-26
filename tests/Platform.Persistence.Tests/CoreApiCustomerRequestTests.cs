using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MinhHuy.AIOffice.Core.Api.Authorization;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CoreApiCustomerRequestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrMissingDefaultFlagReturns404OnEveryRoute(bool explicitlyDisabled)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", "");
            builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", "");
            if (explicitlyDisabled) builder.UseSetting("AIOffice:CustomerRequests:Enabled", "false");
        });
        using var client = factory.CreateClient();
        await Check(await client.PostAsJsonAsync(CustomerRequestAuthenticator.Path, new { }), HttpStatusCode.NotFound);
        var path = $"/api/companies/{Guid.NewGuid():D}/customer-requests";
        await Check(await client.GetAsync(path), HttpStatusCode.NotFound);
        await Check(await client.GetAsync(path + "/YC-0001"), HttpStatusCode.NotFound);
        await Check(await client.PatchAsJsonAsync(path + "/YC-0001", new { }), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SignedHttpCreatesOneTicketFreshNonceReplayReturnsSameAndExactNonceIsDenied()
    {
        await using var fixture = new ApiFixture(); using var client = fixture.Client();
        using var request = fixture.Request();
        var signature = request.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray());
        var firstResponse = await client.SendAsync(request); await Check(firstResponse, HttpStatusCode.OK);
        var first = (await firstResponse.Content.ReadFromJsonAsync<CustomerRequestReceipt>())!;
        Assert.Equal("YC-0001", first.Code); Assert.Equal("new", first.Status);
        using var fresh = fixture.Request(); var replay = await client.SendAsync(fresh); await Check(replay, HttpStatusCode.OK);
        Assert.Equal(first, await replay.Content.ReadFromJsonAsync<CustomerRequestReceipt>());
        using var reused = fixture.Request(); reused.Headers.Clear();
        foreach (var (key, values) in signature) reused.Headers.Add(key, values);
        await Check(await client.SendAsync(reused), HttpStatusCode.Forbidden);
        await using var db = fixture.State.Database(); Assert.Single(db.CustomerRequests); Assert.Single(db.CustomerRequestEvents);
        Assert.DoesNotContain(Convert.ToBase64String(fixture.State.Key), await firstResponse.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("signature", 403)]
    [InlineData("domain", 403)]
    [InlineData("stale", 403)]
    [InlineData("service", 403)]
    [InlineData("group", 403)]
    [InlineData("company", 403)]
    [InlineData("header", 400)]
    [InlineData("query", 400)]
    [InlineData("duplicate-header", 400)]
    [InlineData("non-json", 400)]
    public async Task HttpAuthenticationRefusalsAreBoundedAndStoreNothing(string change, int status)
    {
        await using var fixture = new ApiFixture(); using var client = fixture.Client();
        if (change == "company") { await using var db = fixture.State.Database(); db.Companies.Single().IsActive = false; await db.SaveChangesAsync(); }
        var body = CustomerRequestContractTests.Bytes(change == "group" ? CustomerRequestContractTests.Sample() with { SourceGroupExternalId = "other" } : null);
        var auth = fixture.State.Sign(body, domain: change == "domain" ? "aioffice-group-ingest-v1" : CustomerRequestAuthenticator.Domain,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(change == "stale" ? -6 : 0).ToUnixTimeSeconds(),
            service: change == "service" ? Guid.NewGuid() : null);
        if (change == "signature") auth = auth with { SignatureHex = new string('0', 64) };
        using var request = fixture.Request(body, auth);
        if (change == "header") request.Headers.Remove("X-AIOffice-Request-Service");
        if (change == "query") request.RequestUri = new Uri(CustomerRequestAuthenticator.Path + "?companyId=" + fixture.State.Authority.CompanyId, UriKind.Relative);
        if (change == "duplicate-header") request.Headers.Add("X-AIOffice-Request-Nonce", Guid.NewGuid().ToString("D"));
        if (change == "non-json") request.Content!.Headers.ContentType = new("text/plain");
        await Check(await client.SendAsync(request), (HttpStatusCode)status);
        await using var check = fixture.State.Database(); Assert.Empty(check.CustomerRequests); Assert.Empty(check.CustomerRequestEvents); Assert.Empty(check.CustomerRequestNonces);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("unknown")]
    [InlineData("items")]
    [InlineData("duplicate")]
    public async Task InvalidSignedDtoReturns400AndNoEffects(string invalid)
    {
        await using var fixture = new ApiFixture(); using var client = fixture.Client();
        var node = JsonNode.Parse(CustomerRequestContractTests.Bytes())!;
        if (invalid == "unknown") node["companyId"] = Guid.NewGuid();
        if (invalid == "items") node["items"] = new JsonArray();
        var body = invalid is "null" or "{}" or "[]" ? Encoding.UTF8.GetBytes(invalid) : Encoding.UTF8.GetBytes(node.ToJsonString());
        if (invalid == "duplicate") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"priority\":\"high\"", "\"priority\":\"high\",\"priority\":\"low\"", StringComparison.Ordinal));
        using var request = fixture.Request(body); await Check(await client.SendAsync(request), HttpStatusCode.BadRequest);
        await using var check = fixture.State.Database(); Assert.Empty(check.CustomerRequests); Assert.Empty(check.CustomerRequestNonces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothDeclaredAndStreamedBodiesAreBounded(bool streamed)
    {
        await using var fixture = new ApiFixture(); using var client = fixture.Client(); var bytes = new byte[65537];
        using var request = fixture.Request(bytes);
        if (streamed) request.Content = new StreamContent(new NonSeekableStream(bytes));
        request.Content!.Headers.ContentType = new("application/json");
        await Check(await client.SendAsync(request), HttpStatusCode.RequestEntityTooLarge);
        await using var check = fixture.State.Database(); Assert.Empty(check.CustomerRequests);
    }

    [Fact]
    public async Task PortalMemberFlowReturnsDetailsFiltersAuditsAndConcurrencyConflict()
    {
        await using var fixture = new ApiFixture(); using var client = fixture.Client(portal: true);
        using var request = fixture.Request(CustomerRequestContractTests.Bytes(CustomerRequestContractTests.Sample() with { OpenQuestions = ["Version?"] }));
        await Check(await client.SendAsync(request), HttpStatusCode.OK);
        var page = (await client.GetFromJsonAsync<CustomerRequestPage>(fixture.Path + "?status=needs_clarification&customer=Customer&offset=0&limit=1"))!;
        var item = Assert.Single(page.Items); Assert.Equal("needs_clarification", item.Status);
        var detail = (await client.GetFromJsonAsync<CustomerRequestDetail>(fixture.Path + "/" + item.Code))!;
        Assert.Single(detail.Items); Assert.Single(detail.Evidence); Assert.Equal(new[] { "Version?" }, detail.OpenQuestions); Assert.Single(detail.Events);
        var response = await client.PatchAsJsonAsync(fixture.Path + "/" + item.Code, new { rowVersion = item.RowVersion, status = "in_progress", assigneeUserId = fixture.State.Authority.UserId });
        await Check(response, HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<CustomerRequestListItem>())!;
        Assert.Equal("in_progress", result.Status); Assert.Equal(fixture.State.Authority.UserId, result.AssigneeUserId);
        Assert.Equal("ai_extracted", result.Origin); Assert.NotEqual(item.RowVersion, result.RowVersion);
        await Check(await client.PatchAsJsonAsync(fixture.Path + "/" + item.Code, new { rowVersion = item.RowVersion, status = "done" }), HttpStatusCode.Conflict);
        detail = (await client.GetFromJsonAsync<CustomerRequestDetail>(fixture.Path + "/" + item.Code))!;
        Assert.Equal(3, detail.Events.Count); Assert.Equal(CustomerRequestContractTests.Sample().CustomerDesiredDueText, detail.CustomerDesiredDueText);
        await Check(await client.GetAsync(fixture.Path + "/YC-9999"), HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("header")]
    [InlineData("no-header")]
    [InlineData("revoked")]
    public async Task AllPortalRoutesDenyForeignOrRevokedMembershipWithoutLeakingTicket(string attack)
    {
        await using var fixture = new ApiFixture(); var receipt = await fixture.State.Accept(); using var client = fixture.Client(portal: true);
        var path = fixture.Path;
        if (attack == "route") path = $"/api/companies/{Guid.NewGuid():D}/customer-requests";
        if (attack is "header" or "no-header") client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId);
        if (attack == "header") client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString("D"));
        if (attack == "revoked") { await using var db = fixture.State.Database(); db.CompanyMemberships.Single().IsActive = false; await db.SaveChangesAsync(); }
        await Check(await client.GetAsync(path), HttpStatusCode.Forbidden);
        await Check(await client.GetAsync(path + "/" + receipt.Code), HttpStatusCode.Forbidden);
        await Check(await client.PatchAsJsonAsync(path + "/" + receipt.Code, new { rowVersion = "AAAAAAAAAAA=", status = "done" }), HttpStatusCode.Forbidden);
        await using var check = fixture.State.Database(); Assert.Equal("new", check.CustomerRequests.Single().Status); Assert.Single(check.CustomerRequestEvents);
    }

    [Theory]
    [InlineData("?limit=101")]
    [InlineData("?offset=-1")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?status=approved")]
    [InlineData("?companyId=other")]
    public async Task PortalStrictlyBoundsFilters(string query)
    {
        await using var fixture = new ApiFixture(); using var client = fixture.Client(portal: true);
        await Check(await client.GetAsync(fixture.Path + query), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PortalPatchRejectsAuthorityFieldsAndInactiveOrForeignAssignee()
    {
        await using var fixture = new ApiFixture(); var receipt = await fixture.State.Accept(); using var client = fixture.Client(portal: true);
        var path = fixture.Path + "/" + receipt.Code; var detail = (await client.GetFromJsonAsync<CustomerRequestDetail>(path))!;
        await Check(await client.PatchAsJsonAsync(path, new { rowVersion = detail.Request.RowVersion, status = "done", origin = "human_confirmed" }), HttpStatusCode.BadRequest);
        await Check(await client.PatchAsJsonAsync(path, new { rowVersion = detail.Request.RowVersion, assigneeUserId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        await using var db = fixture.State.Database(); Assert.Single(db.CustomerRequestEvents); Assert.Equal("new", db.CustomerRequests.Single().Status);
    }

    private static async Task Check(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("secretref", body);
        Assert.DoesNotContain("Exception", body); Assert.DoesNotContain("StackTrace", body);
    }

    private sealed class ApiFixture : IAsyncDisposable
    {
        internal readonly CustomerRequestFixture State = new();
        private readonly string databaseVariable = "AIOFFICE_BRIDGE_HTTP_" + Guid.NewGuid().ToString("N");
        internal WebApplicationFactory<Program> Factory { get; }
        internal string Path => $"/api/companies/{State.Authority.CompanyId:D}/customer-requests";
        internal ApiFixture()
        {
            Environment.SetEnvironmentVariable(databaseVariable, "owned-fixture-not-a-connection");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.UseSetting("AIOffice:Authentication:Authority", "https://identity.example.invalid"); builder.UseSetting("AIOffice:Authentication:Audience", "bridge-tests");
                builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", "secretref://env/" + databaseVariable);
                builder.UseSetting("AIOffice:GroupIntake:Enabled", "false"); builder.UseSetting("AIOffice:CustomerRequests:Enabled", "true");
                const string section = "AIOffice:CustomerRequests:Services:0:";
                builder.UseSetting(section + "ServiceId", State.ServiceId.ToString("D")); builder.UseSetting(section + "TenantId", State.Authority.TenantId.ToString("D"));
                builder.UseSetting(section + "CompanyId", State.Authority.CompanyId.ToString("D")); builder.UseSetting(section + "SecretRef", "secretref://env/" + State.Variable);
                builder.UseSetting(section + "AllowedSourceGroupExternalIds:0", "customer-group-1");
                builder.ConfigureServices(services =>
                {
                    services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "BridgeTests"; options.DefaultChallengeScheme = "BridgeTests"; })
                        .AddScheme<AuthenticationSchemeOptions, MemberAuthentication>("BridgeTests", _ => { });
                    services.RemoveAll<DbContextOptions<PlatformDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>(); services.RemoveAll<PlatformDbContext>();
                    services.AddSingleton(State.Options); services.AddScoped(_ => State.Database());
                    var outbox = services.SingleOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MinhHuy.AIOffice.Core.Api.PilotTaskDispatchOutboxHostedService));
                    if (outbox is not null) services.Remove(outbox);
                    services.AddSingleton(State);
                });
            });
        }
        internal HttpClient Client(bool portal = false)
        {
            var client = Factory.CreateClient();
            if (portal) client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, State.Authority.CompanyId.ToString("D"));
            return client;
        }
        internal HttpRequestMessage Request(byte[]? body = null, CustomerRequestSignature? auth = null)
        {
            body ??= CustomerRequestContractTests.Bytes(); auth ??= State.Sign(body);
            var request = new HttpRequestMessage(HttpMethod.Post, CustomerRequestAuthenticator.Path) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("X-AIOffice-Request-Service", auth.ServiceId.ToString("D")); request.Headers.Add("X-AIOffice-Request-Nonce", auth.Nonce.ToString("D"));
            request.Headers.Add("X-AIOffice-Request-Timestamp", auth.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)); request.Headers.Add("X-AIOffice-Request-Signature", auth.SignatureHex);
            return request;
        }
        public async ValueTask DisposeAsync() { await Factory.DisposeAsync(); State.Dispose(); Environment.SetEnvironmentVariable(databaseVariable, null); }
    }

    private sealed class MemberAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, CustomerRequestFixture fixture) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Path.StartsWithSegments("/internal/customer-requests")) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(AuthenticationClaimTypes.IdentityProvider, "PRIVATE_PROVIDER"),
                new Claim(AuthenticationClaimTypes.Subject, "PRIVATE_SUBJECT_" + fixture.Authority.UserId)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
