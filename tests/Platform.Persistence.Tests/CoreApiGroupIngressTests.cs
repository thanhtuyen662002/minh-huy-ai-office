using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CoreApiGroupIngressTests
{
    private const string Path = "/internal/group-ingress/events";

    [Fact]
    public async Task DefaultEndpointIsUnavailableAndNeverClaimsSourceCommit()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        { builder.UseSetting("AIOffice:GroupIntake:Enabled", "false"); builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", ""); });
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(Path, new { text = "owned fixture" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task ServiceSignedHttpIngressAcknowledgesOnlyCommittedProtectedSourceAndDuplicateReconciles()
    {
        await using var fixture = new Fixture(); using var client = fixture.Factory.CreateClient();
        using var request = fixture.Request();
        request.Headers.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.OK);
        var receipt = (await response.Content.ReadFromJsonAsync<GroupIngressCommittedReceipt>())!;
        Assert.Equal(fixture.Auth.Scope, receipt.Source); Assert.False(receipt.WasAlreadyCommitted);
        Assert.Equal(1, receipt.Revision); Assert.Equal(1, receipt.CommittedSequence);
        Assert.Single(await fixture.Auth.Db.GroupMessages.ToListAsync()); Assert.Single(await fixture.Auth.Db.GroupMessageRevisions.ToListAsync());
        Assert.Single(await fixture.Auth.Db.GroupIngressReceipts.ToListAsync()); Assert.Single(await fixture.Auth.Db.GroupIngressOutbox.ToListAsync());
        Assert.Empty(await fixture.Auth.Db.Tasks.ToListAsync()); Assert.Empty(await fixture.Auth.Db.CompanyMemberships.ToListAsync());
        using var replay = fixture.Request(); using var repeated = await client.SendAsync(replay); await BoundedAsync(repeated, HttpStatusCode.OK);
        Assert.Equal(receipt with { WasAlreadyCommitted = true }, await repeated.Content.ReadFromJsonAsync<GroupIngressCommittedReceipt>());
        Assert.Single(await fixture.Auth.Db.GroupIngressOutbox.ToListAsync());
        Assert.DoesNotContain("original", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("epoch-padding")]
    [InlineData("epoch-sign")]
    [InlineData("id-uppercase")]
    [InlineData("query")]
    [InlineData("signature-overflow")]
    public async Task AmbiguousNoncanonicalOrMissingServiceHeadersCannotReachSourceWrites(string change)
    {
        await using var fixture = new Fixture(); using var client = fixture.Factory.CreateClient(); using var request = fixture.Request();
        switch (change)
        {
            case "missing": request.Headers.Remove("X-AIOffice-Group-Service"); break;
            case "duplicate": request.Headers.Add("X-AIOffice-Group-Epoch", "1"); break;
            case "epoch-padding": Replace("X-AIOffice-Group-Epoch", "01"); break;
            case "epoch-sign": Replace("X-AIOffice-Group-Epoch", "+1"); break;
            case "id-uppercase": Replace("X-AIOffice-Group-Service", "AAAAAA11-1111-4111-8111-111111111111"); break;
            case "query": request.RequestUri = new Uri(Path + "?tenantId=" + fixture.Auth.Scope.TenantId, UriKind.Relative); break;
            case "signature-overflow": Replace("X-AIOffice-Group-Signature", new string('A', 65)); break;
        }
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.BadRequest); await fixture.EmptyAsync();
        void Replace(string name, string value) { request.Headers.Remove(name); request.Headers.Add(name, value); }
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("epoch")]
    [InlineData("dm")]
    [InlineData("self")]
    [InlineData("echo")]
    [InlineData("authority")]
    [InlineData("source")]
    [InlineData("expired-lease")]
    [InlineData("malformed-body")]
    public async Task CurrentServiceSourceAndListenerDenialReturnsBoundedResponseWithoutEffects(string change)
    {
        await using var fixture = new Fixture(); using var client = fixture.Factory.CreateClient();
        var payload = fixture.Auth.Payload();
        payload = change switch
        {
            "dm" => payload with { IsGroup = false },
            "self" => payload with { IsSelf = true },
            "echo" => payload with { IsKnownReportEcho = true },
            "source" => payload with { Event = payload.Event with { Identity = payload.Event.Identity with { GroupId = "other" } } },
            _ => payload
        };
        if (change == "authority") { fixture.Auth.Grant.IsEnabled = false; await fixture.Auth.Db.SaveChangesAsync(); }
        if (change == "expired-lease") { fixture.Lease.ExpiresAtUtc = GroupServiceAuthenticatorTests.Fixture.Now; await fixture.Auth.Db.SaveChangesAsync(); }
        using var request = fixture.Request(payload, change == "malformed-body" ? Encoding.UTF8.GetBytes("{\"text\":\"PRIVATE_FIXTURE\"}") : null);
        if (change is "signature" or "epoch")
        {
            var name = change == "signature" ? "X-AIOffice-Group-Signature" : "X-AIOffice-Group-Epoch";
            request.Headers.Remove(name); request.Headers.Add(name, change == "signature" ? new string('0', 64) : "2");
        }
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.Forbidden); await fixture.EmptyAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaredAndChunkedBodyBoundsRefuseBeforePrivateParsing(bool chunked)
    {
        await using var fixture = new Fixture(); using var client = fixture.Factory.CreateClient(); using var request = fixture.Request(body: new byte[65537]);
        if (chunked) request.Content = new StreamContent(new MemoryStream(new byte[65537]));
        request.Content!.Headers.ContentType = new("application/json");
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.RequestEntityTooLarge); await fixture.EmptyAsync();
    }

    [Fact]
    public async Task ConflictingLogicalIdentityReturns409AndKeepsCommittedOriginal()
    {
        await using var fixture = new Fixture(); using var client = fixture.Factory.CreateClient(); using var first = fixture.Request();
        using var accepted = await client.SendAsync(first); await BoundedAsync(accepted, HttpStatusCode.OK);
        var changed = fixture.Auth.Payload() with { Event = fixture.Auth.Payload().Event with { SenderId = "other-sender" } };
        using var request = fixture.Request(changed); using var conflict = await client.SendAsync(request); await BoundedAsync(conflict, HttpStatusCode.Conflict);
        Assert.Single(await fixture.Auth.Db.GroupIngressReceipts.ToListAsync()); Assert.Single(await fixture.Auth.Db.GroupMessageRevisions.ToListAsync());
    }

    [Fact]
    public async Task UnavailablePrivateContentKeyReturnsBounded503WithoutAcknowledgementOrEffects()
    {
        await using var fixture = new Fixture(sourceKeyAvailable: false); using var client = fixture.Factory.CreateClient(); using var request = fixture.Request();
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.ServiceUnavailable); await fixture.EmptyAsync();
    }

    [Fact]
    public async Task NonJsonContentTypeIsRefusedBeforePrivateParsingOrWrites()
    {
        await using var fixture = new Fixture(); using var client = fixture.Factory.CreateClient(); using var request = fixture.Request();
        request.Content!.Headers.ContentType = new("text/plain");
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.UnsupportedMediaType); await fixture.EmptyAsync();
    }

    private static async Task BoundedAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_FIXTURE", text); Assert.DoesNotContain("secretref", text);
        Assert.DoesNotContain("SqlException", text); Assert.DoesNotContain("StackTrace", text);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        private readonly string variable = "AIOFFICE_GROUP_HTTP_" + Guid.NewGuid().ToString("N");
        internal readonly GroupListenerLeaseRecord Lease;
        internal WebApplicationFactory<Program> Factory { get; }
        internal Fixture(bool sourceKeyAvailable = true)
        {
            Lease = new()
            {
                TenantId = Auth.Scope.TenantId,
                CompanyId = Auth.Scope.CompanyId,
                ConnectorAccountId = Auth.Account.Id,
                OwnerId = Auth.Payload().ListenerOwnerId,
                Epoch = 1,
                HeartbeatAtUtc = GroupServiceAuthenticatorTests.Fixture.Now,
                ExpiresAtUtc = GroupServiceAuthenticatorTests.Fixture.Now.AddMinutes(2)
            };
            Auth.Db.Add(Lease); Auth.Db.SaveChanges();
            Environment.SetEnvironmentVariable(variable, "owned-group-http-not-a-real-connection");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", "");
                builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", "secretref://env/" + variable);
                builder.UseSetting("AIOffice:GroupIntake:Enabled", "true");
                builder.UseSetting("AIOffice:GroupIntake:OwnedSyntheticFixture", "true"); builder.UseSetting("AIOffice:GroupIntake:OwnedDisposableFixture", "true");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<PlatformDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>(); services.RemoveAll<PlatformDbContext>();
                    services.AddSingleton(Auth.Options); services.AddScoped(_ => new PlatformDbContext(Auth.Options));
                    services.AddSingleton(new CompositeSecretResolver([Auth.Secrets])); services.AddSingleton<TimeProvider>(Auth.Clock);
                    services.RemoveAll<IGroupSourceKeyProvider>(); services.AddSingleton<IGroupSourceKeyProvider>(new OwnedKeys(sourceKeyAvailable));
                    var outbox = services.SingleOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MinhHuy.AIOffice.Core.Api.PilotTaskDispatchOutboxHostedService));
                    if (outbox is not null) services.Remove(outbox);
                });
            });
        }
        internal HttpRequestMessage Request(GroupIngressPayload? payload = null, byte[]? body = null)
        {
            body ??= JsonSerializer.SerializeToUtf8Bytes(payload ?? Auth.Payload(), GroupServiceAuthenticator.JsonOptions);
            var signature = Auth.Sign(body: body);
            var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-AIOffice-Group-Service", signature.ServiceId.ToString("D"));
            request.Headers.Add("X-AIOffice-Group-Epoch", signature.CredentialEpoch.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-AIOffice-Group-Signed-At", signature.SignedAtUnixSeconds.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-AIOffice-Group-Nonce", signature.Nonce.ToString("D")); request.Headers.Add("X-AIOffice-Group-Signature", signature.SignatureHex);
            return request;
        }
        internal async Task EmptyAsync()
        { Assert.Empty(await Auth.Db.GroupMessages.ToListAsync()); Assert.Empty(await Auth.Db.GroupIngressReceipts.ToListAsync()); Assert.Empty(await Auth.Db.GroupIngressOutbox.ToListAsync()); }
        public async ValueTask DisposeAsync() { await Factory.DisposeAsync(); Auth.Dispose(); Environment.SetEnvironmentVariable(variable, null); }
    }
    private sealed class OwnedKeys(bool available) : IGroupSourceKeyProvider
    {
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default)
            => available ? ValueTask.FromResult(new GroupSourceKeyMaterial("owned", Enumerable.Repeat((byte)0x32, 32).ToArray()))
                : throw new InvalidOperationException("PRIVATE_FIXTURE content key unavailable");
        public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
