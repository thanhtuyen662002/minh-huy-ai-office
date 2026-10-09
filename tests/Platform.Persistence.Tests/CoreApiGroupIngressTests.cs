using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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

    [Theory]
    [InlineData("invalid-key")]
    [InlineData("duplicate-write")]
    [InlineData("empty-scope")]
    public async Task InvalidPrivateEnrollmentFailsBeforeServingAnyRequest(string invalidEnrollment)
    {
        await using var fixture = new Fixture(invalidEnrollment: invalidEnrollment);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Factory.CreateClient());
        Assert.DoesNotContain("secretref://", error.Message);
        Assert.DoesNotContain("PRIVATE_FIXTURE", error.Message);
        await fixture.EmptyAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledRequestBodyHasOwnedDeadlineAndPreservesCallerAbort(bool callerAbort)
    {
        await using var fixture = new Fixture();
        using var request = fixture.Request();
        using var abort = new CancellationTokenSource();
        if (callerAbort) abort.CancelAfter(TimeSpan.FromMilliseconds(100));
        var pending = fixture.Factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST"; context.Request.Path = Path;
            context.Request.ContentType = "application/json"; context.Request.Body = new StalledStream();
            foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        }, abort.Token);
        if (callerAbort)
        {
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(abort.IsCancellationRequested);
            // ASP.NET's Development exception middleware handles an aborted
            // connection as499 with no error page or success receipt.
            Assert.Equal(499, response.Response.StatusCode);
            using var reader = new StreamReader(response.Response.Body);
            await Assert.ThrowsAsync<IOException>(() => reader.ReadToEndAsync());
        }
        else
        {
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(503, response.Response.StatusCode);
            Assert.Equal("no-store", response.Response.Headers.CacheControl);
            using var reader = new StreamReader(response.Response.Body);
            var body = await reader.ReadToEndAsync();
            Assert.Contains("Reconcile the same event.", body);
            Assert.DoesNotContain("Exception", body);
        }
        Assert.Equal(0, fixture.Auth.Secrets.Calls); await fixture.EmptyAsync();
    }

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
        internal Fixture(bool sourceKeyAvailable = true, string? invalidEnrollment = null)
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
                if (invalidEnrollment is not null)
                    for (var index = 0; index < (invalidEnrollment == "duplicate-write" ? 2 : 1); index++)
                    {
                        var section = "AIOffice:GroupIntake:SourceKeys:" + index + ":";
                        builder.UseSetting(section + "TenantId", (invalidEnrollment == "empty-scope" ? Guid.Empty : Auth.Scope.TenantId).ToString("D"));
                        builder.UseSetting(section + "CompanyId", Auth.Scope.CompanyId.ToString("D"));
                        builder.UseSetting(section + "SourceBindingId", Auth.Scope.SourceBindingId.ToString("D"));
                        builder.UseSetting(section + "KeyId", invalidEnrollment == "invalid-key" ? "bad!" : "owned-" + index);
                        builder.UseSetting(section + "SecretRef", "secretref://env/PRIVATE_FIXTURE");
                        builder.UseSetting(section + "IsWriteKey", "true");
                    }
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
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OwnedKeys(bool available) : IGroupSourceKeyProvider
    {
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default)
            => available ? ValueTask.FromResult(new GroupSourceKeyMaterial("owned", Enumerable.Repeat((byte)0x32, 32).ToArray()))
                : throw new InvalidOperationException("PRIVATE_FIXTURE content key unavailable");
        public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
