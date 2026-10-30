using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
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

public sealed class CoreApiBotChannelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeatureDefaultsOffAllRoutesReturn404AndNoStore(bool explicitFalse)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", "");
            builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", "");
            if (explicitFalse) builder.UseSetting("AIOffice:BotChannels:Enabled", "false");
        });
        using var client = factory.CreateClient(); var root = $"/api/companies/{Guid.NewGuid():D}/bot-channels";
        await Check(await client.GetAsync("/internal/bot-config?channel=zalo"), HttpStatusCode.NotFound);
        await Check(await client.PostAsJsonAsync("/internal/bot-status", new { }), HttpStatusCode.NotFound);
        await Check(await client.PostAsJsonAsync($"/internal/bot-commands/{Guid.NewGuid():D}/ack", new { }), HttpStatusCode.NotFound);
        await Check(await client.GetAsync(root), HttpStatusCode.NotFound); await Check(await client.GetAsync(root + "/audit"), HttpStatusCode.NotFound);
        await Check(await client.GetAsync(root + "/zalo/qr"), HttpStatusCode.NotFound);
        await Check(await client.PutAsJsonAsync(root + "/zalo", new { }), HttpStatusCode.NotFound);
        await Check(await client.PutAsJsonAsync(root + "/ai", new { }), HttpStatusCode.NotFound);
        await Check(await client.PostAsync(root + "/ai/test", null), HttpStatusCode.NotFound);
        await Check(await client.PostAsJsonAsync(root + "/zalo/commands", new { }), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SignedHeartbeatAdminConfigurationPullCommandAndAckWorkWithCustomerBridgeDisabled()
    {
        await using var f = new ApiFixture(); using var client = f.Client();
        using var heartbeat = f.Signed("POST", "/internal/bot-status", BotChannelFixture.Bytes(new BotHeartbeat("zalo", "logged_in", [new("tech", "IT"), new("customer", "Khách A")])));
        await Check(await client.SendAsync(heartbeat), HttpStatusCode.OK);
        var view = (await client.GetFromJsonAsync<BotAdminView>(f.Path))!; Assert.True(view.Enabled); Assert.False(view.Ai.ApiKeyConfigured);
        var aiResponse = await client.PutAsJsonAsync(f.Path + "/ai", new BotAiUpdate("0", "https://ai.example.test/v1", "model-a", "PRIVATE-API-KEY"));
        await Check(aiResponse, HttpStatusCode.OK); Assert.DoesNotContain("PRIVATE-API-KEY", await aiResponse.Content.ReadAsStringAsync());
        view = (await client.GetFromJsonAsync<BotAdminView>(f.Path))!;
        var update = new BotChannelUpdate(view.Channels[0].ConfigVersion, true, true, ["17:30", "08:00"], "tech", [new("customer", "Khách A", "Customer A")]);
        await Check(await client.PutAsJsonAsync(f.Path + "/zalo", update), HttpStatusCode.OK);
        var commandResponse = await client.PostAsJsonAsync(f.Path + "/zalo/commands", new { kind = "relogin" }); await Check(commandResponse, HttpStatusCode.OK);
        var command = (await commandResponse.Content.ReadFromJsonAsync<BotCommandView>())!;
        using var pull = f.Signed("GET", "/internal/bot-config?channel=zalo", []); var response = await client.SendAsync(pull); await Check(response, HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Matches("\"createdAt\":\"\\d{4}-\\d{2}-\\d{2}T[0-9:.]+Z\"", raw); Assert.Matches("\"expiresAt\":\"[0-9T:.-]+Z\"", raw); Assert.DoesNotContain("+00:00", raw);
        var config = (await response.Content.ReadFromJsonAsync<BotConfigView>())!; Assert.Equal("PRIVATE-API-KEY", config.Ai.ApiKey); Assert.Equal(command.Id, Assert.Single(config.Commands).Id);
        using var ack = f.Signed("POST", $"/internal/bot-commands/{command.Id:D}/ack", BotChannelFixture.Bytes(new BotCommandAck("zalo")));
        await Check(await client.SendAsync(ack), HttpStatusCode.OK);
        Assert.DoesNotContain("PRIVATE-API-KEY", await client.GetStringAsync(f.Path)); Assert.DoesNotContain("PRIVATE-API-KEY", await client.GetStringAsync(f.Path + "/audit"));
        var test = await client.PostAsync(f.Path + "/ai/test", null); await Check(test, HttpStatusCode.OK);
        Assert.Equal("host_not_allowed", (await test.Content.ReadFromJsonAsync<BotAiProbeResult>())!.Code);
        await Check(await client.PostAsJsonAsync(CustomerRequestAuthenticator.Path, new { }), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChannelUpdateAcceptsBoundedMaximumUnicodeGroupSelections()
    {
        await using var f = new ApiFixture(); using var client = f.Client();
        var groups = Enumerable.Range(0, 100).Select(i => new BotGroup(new string('界', 197) + i.ToString("D3", CultureInfo.InvariantCulture), new string('名', 200))).ToArray();
        using var heartbeat = f.Signed("POST", "/internal/bot-status", BotChannelFixture.Bytes(new BotHeartbeat("zalo", "logged_in", [.. groups, new("tech", "IT")])));
        await Check(await client.SendAsync(heartbeat), HttpStatusCode.OK);
        var input = new BotChannelUpdate("1", true, true, ["08:00"], "tech", groups.Select(g => new BotCustomerGroup(g.ExternalId, g.DisplayName, new string('客', 200))).ToArray());
        await Check(await client.PutAsJsonAsync(f.Path + "/zalo", input), HttpStatusCode.OK);
        var view = (await client.GetFromJsonAsync<BotAdminView>(f.Path))!; Assert.Equal(100, view.Channels[0].CustomerGroups.Length);
    }

    [Theory]
    [InlineData("signature", 403)]
    [InlineData("stale", 403)]
    [InlineData("future", 403)]
    [InlineData("service", 403)]
    [InlineData("wrong-company", 403)]
    [InlineData("company-inactive", 403)]
    [InlineData("query-tamper", 403)]
    [InlineData("method", 403)]
    [InlineData("domain", 403)]
    [InlineData("headers", 400)]
    [InlineData("extra-query", 400)]
    public async Task AuthenticationRefusalsNeverReleaseConfigOrConsumeNonces(string attack, int expected)
    {
        await using var f = new ApiFixture(); using var client = f.Client();
        if (attack is "wrong-company" or "company-inactive")
        { await using var db = f.State.Database(); if (attack == "wrong-company") db.Companies.RemoveRange(db.Companies); else db.Companies.Single().IsActive = false; await db.SaveChangesAsync(); }
        const string target = "/internal/bot-config?channel=zalo";
        var signature = f.State.Sign([], attack == "method" ? "POST" : "GET", attack == "query-tamper" ? "/internal/bot-config?channel=whatsapp" : target);
        if (attack == "signature") signature = signature with { SignatureHex = new string('0', 64) };
        if (attack == "stale") signature = f.State.State.Sign([], method: "GET", path: target, timestamp: DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds());
        if (attack == "future") signature = f.State.State.Sign([], method: "GET", path: target, timestamp: DateTimeOffset.UtcNow.AddMinutes(6).ToUnixTimeSeconds());
        if (attack == "service") signature = signature with { ServiceId = Guid.NewGuid() };
        if (attack == "domain") signature = f.State.State.Sign([], method: "GET", path: target, domain: "aioffice-group-ingest-v1");
        using var request = f.Signed("GET", target, [], signature);
        if (attack == "headers") request.Headers.Add("X-AIOffice-Request-Nonce", Guid.NewGuid().ToString("D"));
        if (attack == "extra-query") request.RequestUri = new Uri(target + "&companyId=" + f.State.Authority.CompanyId, UriKind.Relative);
        await Check(await client.SendAsync(request), (HttpStatusCode)expected);
        await using var check = f.State.Database(); Assert.Empty(check.CustomerRequestNonces); Assert.Empty(check.BotStatuses);
    }

    [Fact]
    public async Task NonceReplayIsRejectedAcrossBotAndExistingCustomerBridgeNamespace()
    {
        await using var f = new ApiFixture(); using var client = f.Client(); const string target = "/internal/bot-config?channel=zalo";
        var signature = f.State.Sign([], "GET", target);
        using (var first = f.Signed("GET", target, [], signature)) await Check(await client.SendAsync(first), HttpStatusCode.OK);
        using (var second = f.Signed("GET", target, [], signature)) await Check(await client.SendAsync(second), HttpStatusCode.Forbidden);
        var bytes = BotChannelFixture.Bytes(new BotHeartbeat("whatsapp", "logged_in", []));
        using var cross = f.Signed("POST", "/internal/bot-status", bytes, f.State.Sign(bytes, "POST", "/internal/bot-status", signature.Nonce));
        await Check(await client.SendAsync(cross), HttpStatusCode.Forbidden);
        var customer = CustomerRequestContractTests.Bytes(); await using var db = f.State.Database();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.State.State.Ingress(db).AcceptAsync(f.State.State.Sign(customer, nonce: signature.Nonce), customer));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("role")]
    [InlineData("route")]
    [InlineData("header")]
    public async Task EveryPortalRouteRequiresFreshAdminInTheSelectedCompany(string change)
    {
        await using var f = new ApiFixture(); using var client = f.Client(); var path = f.Path;
        if (change is "member" or "role")
        { await using var db = f.State.Database(); if (change == "member") db.CompanyMemberships.Single().IsActive = false; else db.RoleAssignments.RemoveRange(db.RoleAssignments); await db.SaveChangesAsync(); }
        if (change == "route") path = $"/api/companies/{Guid.NewGuid():D}/bot-channels";
        if (change == "header") { client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString("D")); }
        await Check(await client.GetAsync(path), HttpStatusCode.Forbidden); await Check(await client.GetAsync(path + "/audit"), HttpStatusCode.Forbidden);
        await Check(await client.GetAsync(path + "/zalo/qr"), HttpStatusCode.Forbidden);
        await Check(await client.PutAsJsonAsync(path + "/zalo", new { }), HttpStatusCode.Forbidden);
        await Check(await client.PutAsJsonAsync(path + "/ai", new { }), HttpStatusCode.Forbidden);
        await Check(await client.PostAsync(path + "/ai/test", null), HttpStatusCode.Forbidden);
        await Check(await client.PostAsJsonAsync(path + "/zalo/commands", new { }), HttpStatusCode.Forbidden);
        await using var check = f.State.Database(); Assert.Empty(check.BotChannelAudits); Assert.Empty(check.BotAiSettings);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("authority")]
    [InlineData("oversized")]
    [InlineData("utf8")]
    public async Task HeartbeatBodyIsStrictBoundedAndUntrusted(string attack)
    {
        await using var f = new ApiFixture(); using var client = f.Client();
        var bytes = Encoding.UTF8.GetBytes(attack switch
        {
            "duplicate" => "{\"channel\":\"zalo\",\"channel\":\"whatsapp\",\"loginState\":\"logged_in\",\"groups\":[]}",
            "authority" => "{\"channel\":\"zalo\",\"loginState\":\"logged_in\",\"groups\":[],\"companyId\":\"foreign\"}",
            "oversized" => new string(' ', BotChannelJson.MaximumBodyBytes + 1),
            _ => "{}"
        });
        if (attack == "utf8") bytes = [0xff];
        using var request = f.Signed("POST", "/internal/bot-status", bytes); await Check(await client.SendAsync(request), attack == "oversized" ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest);
        await using var db = f.State.Database(); Assert.Empty(db.BotStatuses); Assert.Empty(db.CustomerRequestNonces);
    }

    [Fact]
    public async Task CompanyServiceCannotAcknowledgeOtherCompanyCommand()
    {
        await using var f = new ApiFixture(); using var client = f.Client(); var foreign = MemberDirectoryFixture.Authority();
        await using (var db = f.State.Database())
        {
            MemberDirectoryFixture.Seed(db, foreign); await db.SaveChangesAsync();
            await f.State.Service(db).EnqueueAsync(foreign, "zalo", "relogin");
        }
        Guid id; await using (var db = f.State.Database()) id = db.BotCommands.Single().Id;
        using var request = f.Signed("POST", $"/internal/bot-commands/{id:D}/ack", BotChannelFixture.Bytes(new BotCommandAck("zalo")));
        await Check(await client.SendAsync(request), HttpStatusCode.NotFound);
        await using var check = f.State.Database(); Assert.Null(check.BotCommands.Single().AcknowledgedAt);
    }

    private static async Task Check(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("secretref", body); Assert.DoesNotContain("Exception", body); Assert.DoesNotContain("StackTrace", body);
    }

    private sealed class ApiFixture : IAsyncDisposable
    {
        internal readonly BotChannelFixture State = new();
        private readonly string databaseVariable = "AIOFFICE_BOT_HTTP_" + Guid.NewGuid().ToString("N");
        private readonly WebApplicationFactory<Program> factory;
        internal string Path => $"/api/companies/{State.Authority.CompanyId:D}/bot-channels";
        internal ApiFixture()
        {
            Environment.SetEnvironmentVariable(databaseVariable, "fixture-not-a-connection");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development"); builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.UseSetting("AIOffice:Authentication:Authority", "https://identity.example.invalid"); builder.UseSetting("AIOffice:Authentication:Audience", "bot-tests");
                builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", "secretref://env/" + databaseVariable);
                builder.UseSetting("AIOffice:GroupIntake:Enabled", "false"); builder.UseSetting("AIOffice:CustomerRequests:Enabled", "false"); builder.UseSetting("AIOffice:BotChannels:Enabled", "true");
                builder.UseSetting("AIOffice:BotChannels:EncryptionKeySecretRef", "secretref://env/" + State.State.Variable);
                const string section = "AIOffice:CustomerRequests:Services:0:";
                builder.UseSetting(section + "ServiceId", State.State.ServiceId.ToString("D")); builder.UseSetting(section + "TenantId", State.Authority.TenantId.ToString("D"));
                builder.UseSetting(section + "CompanyId", State.Authority.CompanyId.ToString("D")); builder.UseSetting(section + "SecretRef", "secretref://env/" + State.State.Variable);
                builder.UseSetting(section + "AllowedSourceGroupExternalIds:0", "customer-group-1");
                builder.ConfigureServices(services =>
                {
                    services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "BotTests"; options.DefaultChallengeScheme = "BotTests"; })
                        .AddScheme<AuthenticationSchemeOptions, MemberAuthentication>("BotTests", _ => { });
                    services.RemoveAll<DbContextOptions<PlatformDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>(); services.RemoveAll<PlatformDbContext>();
                    services.AddSingleton(State.State.Options); services.AddScoped(_ => State.Database()); services.AddSingleton(State);
                    var outbox = services.SingleOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MinhHuy.AIOffice.Core.Api.PilotTaskDispatchOutboxHostedService));
                    if (outbox is not null) services.Remove(outbox);
                });
            });
        }
        internal HttpClient Client() { var client = factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, State.Authority.CompanyId.ToString("D")); return client; }
        internal HttpRequestMessage Signed(string method, string target, byte[] body, CustomerRequestSignature? auth = null)
        {
            auth ??= State.Sign(body, method, target); var request = new HttpRequestMessage(new HttpMethod(method), target);
            if (method != "GET") { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
            request.Headers.Add("X-AIOffice-Request-Service", auth.ServiceId.ToString("D")); request.Headers.Add("X-AIOffice-Request-Nonce", auth.Nonce.ToString("D"));
            request.Headers.Add("X-AIOffice-Request-Timestamp", auth.Timestamp.ToString(CultureInfo.InvariantCulture)); request.Headers.Add("X-AIOffice-Request-Signature", auth.SignatureHex); return request;
        }
        public async ValueTask DisposeAsync() { await factory.DisposeAsync(); State.Dispose(); Environment.SetEnvironmentVariable(databaseVariable, null); }
    }
    private sealed class MemberAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, BotChannelFixture fixture) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Path.StartsWithSegments("/internal")) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(AuthenticationClaimTypes.IdentityProvider, "PRIVATE_PROVIDER"),
                new Claim(AuthenticationClaimTypes.Subject, "PRIVATE_SUBJECT_" + fixture.Authority.UserId)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
