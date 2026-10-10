using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CoreApiGroupListenerTests
{
    private const string Path = "/internal/group-ingress/listener";

    [Fact]
    public async Task DefaultListenerEndpointIsUnavailableWithNoStore()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        { builder.UseSetting("AIOffice:GroupIntake:Enabled", "false"); builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", ""); });
        using var client = factory.CreateClient(); using var response = await client.PostAsJsonAsync(Path, new { ownerId = Guid.NewGuid() });
        await BoundedAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ServiceListenerHttpAckAndStableReplayPersistOnlyLeaseCommandAndCoverage()
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var client = fixture.Factory.CreateClient();
        var nonce = Guid.NewGuid(); using var request = Request(fixture, nonce: nonce);
        request.Headers.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString("D")); request.Headers.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.OK);
        var receipt = (await response.Content.ReadFromJsonAsync<GroupListenerCommittedReceipt>())!;
        Assert.Equal(fixture.Auth.Account.Id, receipt.Lease.Account.ConnectorAccountId); Assert.Equal(1, receipt.Lease.Epoch); Assert.False(receipt.WasAlreadyCommitted);
        using var replay = Request(fixture, nonce: nonce); using var repeated = await client.SendAsync(replay); await BoundedAsync(repeated, HttpStatusCode.OK);
        Assert.Equal(receipt with { WasAlreadyCommitted = true }, await repeated.Content.ReadFromJsonAsync<GroupListenerCommittedReceipt>());
        Assert.Single(fixture.Auth.Db.GroupListenerLeases); Assert.Single(fixture.Auth.Db.GroupListenerCommandReceipts); Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps);
        await fixture.EmptyAsync(); Assert.Empty(fixture.Auth.Db.Tasks); Assert.Empty(fixture.Auth.Db.CompanyMemberships);
    }

    [Fact]
    public async Task ConflictingNonceIsBounded409AndExpiredReplayCannotExtendLeaseThroughHttp()
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var client = fixture.Factory.CreateClient(); var nonce = Guid.NewGuid();
        using var acquire = Request(fixture, nonce: nonce); using var committed = await client.SendAsync(acquire); await BoundedAsync(committed, HttpStatusCode.OK);
        using var conflict = Request(fixture, new(fixture.Auth.Payload().ListenerOwnerId, GroupListenerOperation.Stop, 1), nonce);
        using var refused = await client.SendAsync(conflict); await BoundedAsync(refused, HttpStatusCode.Conflict);
        fixture.Auth.Clock.Current = GroupServiceAuthenticatorTests.Fixture.Now.AddSeconds(31);
        using var replay = Request(fixture, nonce: nonce); using var expired = await client.SendAsync(replay); await BoundedAsync(expired, HttpStatusCode.Forbidden);
        Assert.Equal(1, (await fixture.Auth.Db.GroupListenerLeases.AsNoTracking().SingleAsync()).Epoch);
        Assert.Single(fixture.Auth.Db.GroupListenerCommandReceipts); Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("epoch-padding")]
    [InlineData("query")]
    [InlineData("uppercase-id")]
    [InlineData("signature-overflow")]
    public async Task NoncanonicalServiceTransportCannotReachListenerCommit(string change)
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var client = fixture.Factory.CreateClient(); using var request = Request(fixture);
        if (change == "missing") request.Headers.Remove("X-AIOffice-Group-Service");
        if (change == "duplicate") request.Headers.Add("X-AIOffice-Group-Epoch", "1");
        if (change == "epoch-padding") Replace("X-AIOffice-Group-Epoch", "01");
        if (change == "query") request.RequestUri = new Uri(Path + "?accountId=forged", UriKind.Relative);
        if (change == "uppercase-id") Replace("X-AIOffice-Group-Service", "AAAAAA11-1111-4111-8111-111111111111");
        if (change == "signature-overflow") Replace("X-AIOffice-Group-Signature", new string('A', 65));
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.BadRequest); await EmptyListenerAsync(fixture);
        void Replace(string name, string value) { request.Headers.Remove(name); request.Headers.Add(name, value); }
    }

    [Theory]
    [InlineData("event-domain")]
    [InlineData("portal-only")]
    [InlineData("ungranted")]
    [InlineData("unknown-fields")]
    [InlineData("invalid-operation")]
    [InlineData("invalid-utf8")]
    public async Task WrongAuthenticationOrCommandCannotCreateOwnership(string change)
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var client = fixture.Factory.CreateClient();
        var command = new GroupListenerCommand(fixture.Auth.Payload().ListenerOwnerId, change == "invalid-operation" ? (GroupListenerOperation)99 : GroupListenerOperation.Acquire, 0);
        byte[]? body = null;
        if (change == "unknown-fields") body = JsonSerializer.SerializeToUtf8Bytes(new { identity = fixture.Auth.External, command, tenantId = fixture.Auth.Scope.TenantId });
        if (change == "invalid-utf8") body = [0xff];
        if (change == "ungranted") { fixture.Auth.Grant.IsEnabled = false; await fixture.Auth.Db.SaveChangesAsync(); }
        using var request = Request(fixture, command, body: body, eventDomain: change == "event-domain");
        if (change == "portal-only")
        {
            foreach (var name in request.Headers.Select(x => x.Key).ToArray()) request.Headers.Remove(name);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owned-not-a-real-portal-token");
        }
        using var response = await client.SendAsync(request);
        await BoundedAsync(response, change == "portal-only" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden); await EmptyListenerAsync(fixture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListenerActualBodyAndDeclaredLengthAreBoundedBeforeAuthentication(bool declared)
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var client = fixture.Factory.CreateClient();
        using var request = Request(fixture, body: new byte[8193]);
        if (declared)
        {
            request.Content!.Headers.ContentLength = 8193;
            using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.RequestEntityTooLarge);
        }
        else
        {
            var response = await fixture.Factory.Server.SendAsync(context =>
            {
                context.Request.Method = "POST"; context.Request.Path = Path;
                context.Request.ContentType = "application/json"; context.Request.ContentLength = null;
                context.Request.Body = new MemoryStream(new byte[8193]);
                foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
            });
            Assert.Equal(413, response.Response.StatusCode); Assert.Equal("no-store", response.Response.Headers.CacheControl);
        }
        Assert.Equal(0, fixture.Auth.Secrets.Calls); await EmptyListenerAsync(fixture);
    }

    [Fact]
    public async Task NonJsonListenerBodyIsBounded415BeforeAuthentication()
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var client = fixture.Factory.CreateClient(); using var request = Request(fixture);
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(0, fixture.Auth.Secrets.Calls); await EmptyListenerAsync(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledListenerBodyHonorsOwnedDeadlineAndOriginalCallerCancellation(bool callerAbort)
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var request = Request(fixture);
        using var abort = new CancellationTokenSource();
        if (callerAbort) abort.CancelAfter(TimeSpan.FromMilliseconds(100));
        var response = await fixture.Factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST"; context.Request.Path = Path; context.Request.ContentType = "application/json";
            context.Request.Body = new StalledStream();
            foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        }, abort.Token).WaitAsync(TimeSpan.FromSeconds(15));
        using var reader = new StreamReader(response.Response.Body);
        if (callerAbort)
        {
            Assert.True(abort.IsCancellationRequested); Assert.Equal(499, response.Response.StatusCode);
            await Assert.ThrowsAsync<IOException>(() => reader.ReadToEndAsync());
        }
        else
        {
            Assert.Equal(503, response.Response.StatusCode); Assert.Equal("no-store", response.Response.Headers.CacheControl);
            var body = await reader.ReadToEndAsync(); Assert.Contains("Reconcile the same command.", body); Assert.DoesNotContain("Exception", body);
        }
        Assert.Equal(0, fixture.Auth.Secrets.Calls); await EmptyListenerAsync(fixture);
    }

    private static HttpRequestMessage Request(CoreApiGroupIngressTests.Fixture fixture, GroupListenerCommand? command = null, Guid? nonce = null,
        byte[]? body = null, bool eventDomain = false)
    {
        body ??= JsonSerializer.SerializeToUtf8Bytes(new GroupListenerPayload(fixture.Auth.External,
            command ?? new(fixture.Auth.Payload().ListenerOwnerId, GroupListenerOperation.Acquire, 0)), GroupServiceAuthenticator.JsonOptions);
        var signature = new GroupServiceSignature(fixture.Auth.Service.Id, fixture.Auth.Service.CredentialEpoch, fixture.Auth.Clock.Current.ToUnixTimeSeconds(), nonce ?? Guid.NewGuid(), "");
        signature = signature with
        {
            SignatureHex = Convert.ToHexString(HMACSHA256.HashData(fixture.Auth.Secrets.Key,
            eventDomain ? GroupServiceAuthenticator.SigningBytes(signature, body) : GroupServiceAuthenticator.ListenerSigningBytes(signature, body)))
        };
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-AIOffice-Group-Service", signature.ServiceId.ToString("D")); request.Headers.Add("X-AIOffice-Group-Epoch", signature.CredentialEpoch.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-AIOffice-Group-Signed-At", signature.SignedAtUnixSeconds.ToString(CultureInfo.InvariantCulture)); request.Headers.Add("X-AIOffice-Group-Nonce", signature.Nonce.ToString("D"));
        request.Headers.Add("X-AIOffice-Group-Signature", signature.SignatureHex); return request;
    }

    [Theory]
    [InlineData(Path, false)]
    [InlineData(Path, true)]
    [InlineData("/internal/group-ingress/events", false)]
    [InlineData("/internal/group-ingress/events", true)]
    public async Task BodyTransportFaultIsBoundedBeforeAuthenticationAndClearsPartialCapture(string path, bool partial)
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var request = Request(fixture);
        using var stream = new FailingStream(partial);
        var response = await fixture.Factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST"; context.Request.Path = path; context.Request.ContentType = "application/json";
            context.Request.Body = stream;
            foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        });
        using var reader = new StreamReader(response.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Equal(503, response.Response.StatusCode); Assert.Equal("no-store", response.Response.Headers.CacheControl);
        Assert.DoesNotContain("PRIVATE_FIXTURE", body); Assert.DoesNotContain("IOException", body);
        Assert.DoesNotContain("Exception", body); Assert.Contains("Invalid", body);
        Assert.Equal(0, fixture.Auth.Secrets.Calls); await EmptyListenerAsync(fixture);
        Assert.All(stream.Captured.ToArray(), value => Assert.Equal((byte)0, value));
    }
    private static async Task EmptyListenerAsync(CoreApiGroupIngressTests.Fixture fixture)
    {
        Assert.Empty(await fixture.Auth.Db.GroupListenerLeases.ToArrayAsync()); Assert.Empty(await fixture.Auth.Db.GroupAccountCoverageGaps.ToArrayAsync());
        Assert.Empty(await fixture.Auth.Db.GroupListenerCommandReceipts.ToArrayAsync()); await fixture.EmptyAsync();
    }
    private static async Task BoundedAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("secretref", body); Assert.DoesNotContain("Exception", body); Assert.DoesNotContain("PRIVATE_FIXTURE", body);
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
    private sealed class FailingStream(bool partial) : Stream
    {
        internal Memory<byte> Captured { get; private set; }
        private int calls;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++calls == 1)
            {
                Captured = buffer;
                if (partial) { "PRIVATE_FIXTURE"u8.CopyTo(buffer.Span); return ValueTask.FromResult(15); }
            }
            throw new IOException("PRIVATE_FIXTURE_BODY_TRANSPORT");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
