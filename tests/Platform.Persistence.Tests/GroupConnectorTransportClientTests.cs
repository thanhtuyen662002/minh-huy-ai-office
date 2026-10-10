using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupConnectorTransportClientTests
{
    [Fact]
    public async Task ExactPreparedEventRetryUsesSameCapturedBodyNonceAndIndependentHmacVector()
    {
        using var fixture = new Fixture(); var calls = new List<(byte[] Body, string Nonce, string Signature)>();
        using var client = fixture.Client(async (request, cancellation) =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellation);
            Assert.Equal("/internal/group-ingress/events", request.RequestUri!.AbsolutePath);
            Assert.False(request.Headers.Contains("Authorization")); Assert.False(request.Headers.Contains("X-User-Id"));
            var nonce = request.Headers.GetValues("X-AIOffice-Group-Nonce").Single();
            var signature = request.Headers.GetValues("X-AIOffice-Group-Signature").Single();
            var vector = Encoding.ASCII.GetBytes($"aioffice-group-ingest-v1\n{fixture.Auth.Service.Id:D}\n1\n{Fixture.Now.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(body))}");
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(fixture.Auth.Secrets.Key, vector)), signature);
            var parsed = GroupServiceAuthenticator.Parse(body);
            Assert.Equal(fixture.Auth.Payload().Event.Identity, parsed.Event.Identity);
            calls.Add((body, nonce, signature)); return fixture.Reply(request, fixture.EventReceipt);
        });
        using var prepared = await client.PrepareEventAsync(fixture.Admission());
        Assert.Equal(fixture.EventReceipt, await client.SendEventAsync(prepared));
        Assert.Equal(fixture.EventReceipt, await client.SendEventAsync(prepared));
        Assert.Equal(2, calls.Count); Assert.Equal(calls[0].Body, calls[1].Body); Assert.Equal(calls[0].Nonce, calls[1].Nonce); Assert.Equal(calls[0].Signature, calls[1].Signature);
        Assert.Equal(1, fixture.Auth.Secrets.Calls);
    }

    [Fact]
    public async Task ListenerUsesSeparateSignatureDomainAndExactQualifiedAccountLease()
    {
        using var fixture = new Fixture(); var command = new GroupListenerCommand(Guid.NewGuid(), GroupListenerOperation.Acquire, 0);
        using var client = fixture.Client(async (request, cancellation) =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellation);
            var signature = request.Headers.GetValues("X-AIOffice-Group-Signature").Single();
            var nonce = request.Headers.GetValues("X-AIOffice-Group-Nonce").Single();
            var vector = Encoding.ASCII.GetBytes($"aioffice-group-listener-v1\n{fixture.Auth.Service.Id:D}\n1\n{Fixture.Now.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(body))}");
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(fixture.Auth.Secrets.Key, vector)), signature);
            Assert.Equal("/internal/group-ingress/listener", request.RequestUri!.AbsolutePath);
            Assert.Equal(command, GroupServiceAuthenticator.ParseListener(body).Command);
            return fixture.Reply(request, fixture.ListenerReceipt(command));
        });
        using var listener = await client.PrepareListenerAsync(fixture.Enrollment, command);
        Assert.Equal(fixture.ListenerReceipt(command), await client.SendListenerAsync(listener));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.SendEventAsync(listener));
        using var source = await client.PrepareEventAsync(fixture.Admission());
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.SendListenerAsync(source));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("empty-message")]
    [InlineData("revision")]
    [InlineData("sequence")]
    [InlineData("offset")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("utf8")]
    [InlineData("declared-overflow")]
    [InlineData("actual-overflow")]
    [InlineData("content-type")]
    [InlineData("cache")]
    [InlineData("redirect-origin")]
    public async Task MalformedOrForeignSuccessNeverBecomesCommitAck(string change)
    {
        using var fixture = new Fixture();
        using var client = fixture.Client((request, _) =>
        {
            var value = fixture.EventReceipt;
            if (change == "foreign") value = value with { Source = value.Source with { CompanyId = Guid.NewGuid() } };
            if (change == "empty-message") value = value with { MessageId = Guid.Empty };
            if (change == "revision") value = value with { Revision = 0 };
            if (change == "sequence") value = value with { CommittedSequence = 0 };
            if (change == "offset") value = value with { CommittedAtUtc = Fixture.Now.ToOffset(TimeSpan.FromHours(7)) };
            var response = fixture.Reply(request, value); var json = JsonSerializer.Serialize(value, GroupServiceAuthenticator.JsonOptions);
            if (change == "missing") json = "{}";
            if (change == "duplicate") json = json.Replace("\"revision\":1", "\"revision\":1,\"revisi\\u006fn\":1", StringComparison.Ordinal);
            if (change == "extra") json = json[..^1] + ",\"private\":\"PRIVATE_DIAGNOSTIC\"}";
            if (change == "actual-overflow") json = new string(' ', 8193);
            response.Content.Dispose(); response.Content = new ByteArrayContent(change == "utf8" ? [0xC3, 0x28] : Encoding.UTF8.GetBytes(json));
            response.Content.Headers.ContentType = new("application/json");
            if (change == "declared-overflow") response.Content.Headers.ContentLength = 8193;
            if (change == "content-type") response.Content.Headers.ContentType = new("text/plain");
            if (change == "cache") response.Headers.CacheControl = new() { NoStore = false };
            if (change == "redirect-origin") response.RequestMessage = new(HttpMethod.Post, "https://foreign.invalid/");
            return Task.FromResult(response);
        });
        using var prepared = await client.PrepareEventAsync(fixture.Admission());
        var error = await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.SendEventAsync(prepared));
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Theory]
    [InlineData(302)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task NonSuccessPreservesStatusAndNeverReadsPrivateErrorBody(int status)
    {
        using var fixture = new Fixture(); var content = new FaultContent();
        using var client = fixture.Client((request, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request, Content = content }));
        using var prepared = await client.PrepareEventAsync(fixture.Admission());
        var error = await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.SendEventAsync(prepared));
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.False(content.Read); Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public async Task ScopedPreparationRefusesBeforeResolvingSigningKeyAndDisposalZeroesBody()
    {
        using var fixture = new Fixture(); using var client = fixture.Client((_, _) => throw new InvalidOperationException("No HTTP expected"));
        var enrollment = fixture.Enrollment with { Source = fixture.Enrollment.Source with { Scope = fixture.Auth.Scope with { CompanyId = Guid.NewGuid() } } };
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.PrepareListenerAsync(enrollment, new(Guid.NewGuid(), GroupListenerOperation.Acquire, 0)));
        Assert.Equal(0, fixture.Auth.Secrets.Calls);
        using var prepared = await client.PrepareEventAsync(fixture.Admission());
        var field = typeof(GroupConnectorPreparedRequest).GetField("body", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var alias = (byte[])field.GetValue(prepared)!; Assert.Contains(alias, value => value != 0);
        prepared.Dispose(); Assert.All(alias, value => Assert.Equal((byte)0, value));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.SendEventAsync(prepared));
    }

    [Fact]
    public async Task CallerCancellationWhileHeadersAreHeldRefusesLateAckAndDisposesLateResponse()
    {
        using var fixture = new Fixture(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? sent = null;
        using var client = fixture.Client((request, _) => { sent = request; started.SetResult(); return release.Task; });
        using var prepared = await client.PrepareEventAsync(fixture.Admission()); using var cancel = new CancellationTokenSource();
        var send = client.SendEventAsync(prepared, cancel.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(2)));
        var content = new DisposalContent(); var late = fixture.Reply(sent!, fixture.EventReceipt); late.Content.Dispose(); late.Content = content;
        release.SetResult(late); await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(send.IsCanceled); Assert.False(content.Read);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("expired-before-key")]
    [InlineData("expired-after-key")]
    public async Task EventPreparationRechecksActualAdmittedLeaseAndClientProfile(string change)
    {
        using var fixture = new Fixture(); var admission = fixture.Admission();
        using var client = new GroupConnectorTransportClient(new(change == "live" ? "https://owned.invalid/" : "http://127.0.0.1/"),
            fixture.Binding, fixture.Secrets, fixture.Clock,
            change == "live" ? GroupIngressRuntimePolicy.Live : GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true),
            new Handler((_, _) => throw new InvalidOperationException("No HTTP expected")));
        if (change == "expired-before-key") fixture.Clock.Current = Fixture.Now.AddSeconds(30);
        if (change == "expired-after-key") fixture.Auth.Secrets.BeforeResolution = () => fixture.Clock.Current = Fixture.Now.AddSeconds(30);
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.PrepareEventAsync(admission));
        Assert.Equal(change == "expired-after-key" ? 1 : 0, fixture.Auth.Secrets.Calls);
    }

    [Fact]
    public async Task OwnedDeadlineWhileHeadersAreHeldReturnsGenericUnknownAndDisposesLateResponse()
    {
        using var fixture = new Fixture(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); HttpRequestMessage? sent = null;
        using var client = fixture.Client((request, _) => { sent = request; started.SetResult(); return release.Task; });
        using var prepared = await client.PrepareEventAsync(fixture.Admission()); var send = client.SendEventAsync(prepared);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); fixture.Clock.FireDeadline();
        var error = await Assert.ThrowsAsync<GroupConnectorTransportException>(() => send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(error.StatusCode);
        var content = new DisposalContent(); var late = fixture.Reply(sent!, fixture.EventReceipt); late.Content.Dispose(); late.Content = content;
        release.SetResult(late); await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.False(content.Read);
    }

    [Theory]
    [InlineData("http://example.invalid/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("https://user:password@example.invalid/")]
    [InlineData("https://example.invalid/path")]
    [InlineData("https://example.invalid/?secret=marker")]
    [InlineData("https://example.invalid/#marker")]
    public void LiveTransportRefusesUnqualifiedOrigins(string url)
    {
        using var fixture = new Fixture();
        Assert.Throws<GroupConnectorTransportException>(() => new GroupConnectorTransportClient(new(url), fixture.Binding,
            fixture.Secrets, fixture.Clock, GroupIngressRuntimePolicy.Live));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerAbortDuringNoncooperativeBodyRejectsLateAckAndRezerosAliasedReadTarget(bool lateFault)
    {
        using var fixture = new Fixture(); var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture.EventReceipt, GroupServiceAuthenticator.JsonOptions);
        using var held = new HeldStream(bytes);
        using var client = fixture.Client((request, _) =>
        {
            var response = fixture.Reply(request, fixture.EventReceipt); response.Content.Dispose();
            response.Content = new StreamContent(held); response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        });
        using var prepared = await client.PrepareEventAsync(fixture.Admission()); using var cancel = new CancellationTokenSource();
        var send = client.SendEventAsync(prepared, cancel.Token);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally { held.Release.TrySetResult(lateFault); }
        await held.Written.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (held.Target.ToArray().Any(value => value != 0) && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.All(held.Target.ToArray(), value => Assert.Equal((byte)0, value)); Assert.True(send.IsCanceled);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("owner")]
    [InlineData("epoch")]
    [InlineData("expired")]
    [InlineData("long")]
    [InlineData("future")]
    [InlineData("offset")]
    public async Task InvalidListenerSuccessNeverAuthorizesProcessOwnership(string change)
    {
        using var fixture = new Fixture(); var command = new GroupListenerCommand(Guid.NewGuid(), GroupListenerOperation.Renew, 1);
        using var client = fixture.Client((request, _) =>
        {
            var value = fixture.ListenerReceipt(command); var lease = value.Lease;
            if (change == "account") lease = lease with { Account = lease.Account with { ConnectorAccountId = Guid.NewGuid() } };
            if (change == "owner") lease = lease with { OwnerId = Guid.NewGuid() };
            if (change == "epoch") lease = lease with { Epoch = 2 };
            if (change == "expired") lease = lease with { ExpiresAtUtc = Fixture.Now };
            if (change == "long") lease = lease with { ExpiresAtUtc = Fixture.Now.AddSeconds(31) };
            if (change == "future") lease = lease with { HeartbeatAtUtc = Fixture.Now.AddTicks(1) };
            if (change == "offset") lease = lease with { HeartbeatAtUtc = Fixture.Now.ToOffset(TimeSpan.FromHours(7)) };
            return Task.FromResult(fixture.Reply(request, value with { Lease = lease }));
        });
        using var prepared = await client.PrepareListenerAsync(fixture.Enrollment, command);
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.SendListenerAsync(prepared));
    }

    private sealed class Fixture : IDisposable
    {
        internal static DateTimeOffset Now => GroupServiceAuthenticatorTests.Fixture.Now;
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        internal readonly ControlledClock Clock = new();
        internal CompositeSecretResolver Secrets => new([Auth.Secrets]);
        internal GroupConnectorSigningBinding Binding => new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, 1, SecretReference.Parse("secretref://env/OWNED_GROUP_KEY"));
        internal GroupConnectorEnrollment Enrollment
        {
            get
            {
                var artifact = new GroupConnectorArtifact(Auth.External.Provider, Auth.Account.PackageVersion, Auth.Account.GitCommit);
                return new(new(Auth.Service.Id, 1), new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, 1, true),
                    new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, Auth.Scope.SourceBindingId, GroupServiceCapability.Ingest, 1, true),
                    new(Auth.Scope, Auth.Account.Id, Auth.External, "Owned transport", 1, 0, true),
                    new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id, Auth.External.AccountId, artifact, GroupQualificationEnvironment.Synthetic, []), artifact);
            }
        }
        internal GroupConnectorSpoolAdmission Admission() => GroupConnectorSpoolAdmission.Filter(Enrollment, Auth.Payload(),
            new(new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id), Auth.Payload().ListenerOwnerId, 1, Now, Now.AddSeconds(30)), Now,
            GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true));
        internal GroupIngressCommittedReceipt EventReceipt => new(Auth.Scope, Guid.Parse("11111111-1111-1111-1111-111111111111"), 1, 1, Now, false);
        internal GroupListenerCommittedReceipt ListenerReceipt(GroupListenerCommand command) => new(new(new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id),
            command.OwnerId, 1, Now, Now.AddSeconds(30)), true, true, Now, false);
        internal GroupConnectorTransportClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) =>
            new(new("http://127.0.0.1/"), Binding, Secrets, Clock, GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), new Handler(reply));
        internal HttpResponseMessage Reply(HttpRequestMessage request, object value) => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(JsonSerializer.Serialize(value, GroupServiceAuthenticator.JsonOptions), Encoding.UTF8, "application/json"),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };
        public void Dispose() => Auth.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken); }
    private class FaultContent : HttpContent
    {
        internal bool Read;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Read = true; throw new IOException("PRIVATE_DIAGNOSTIC"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class DisposalContent : FaultContent
    {
        internal TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing) { base.Dispose(disposing); Disposed.TrySetResult(); }
    }
    private sealed class HeldStream(byte[] bytes) : Stream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Memory<byte> Target { get; private set; }
        private bool read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) return 0;
            read = true; Target = buffer; Started.TrySetResult(); var fault = await Release.Task;
            bytes.CopyTo(buffer); Written.TrySetResult();
            if (fault) throw new IOException("PRIVATE_LATE_STREAM_FAULT");
            return bytes.Length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
    private sealed class ControlledClock : TimeProvider
    {
        private ControlledTimer? current;
        internal DateTimeOffset Current = Fixture.Now;
        public override DateTimeOffset GetUtcNow() => Current;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => current = new(callback, state);
        internal void FireDeadline() => current!.Fire();
        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
