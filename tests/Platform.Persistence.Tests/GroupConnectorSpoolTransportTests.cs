using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupConnectorSpoolTransportTests
{
    [Fact]
    public async Task CommitReplyLossReopensExactBacklogAndReplaysOriginalLogicalEventUnderNewOwner()
    {
        using var fixture = new Fixture(); var spool = fixture.Open();
        var reference = fixture.Append(spool); var bytes = fixture.Bytes(); var retainedPath = fixture.ItemPath;
        GroupIngressPayload? committedPayload = null; string? firstNonce = null; var effects = 0;
        using (var lost = fixture.Client(async (request, token) =>
        {
            committedPayload = GroupServiceAuthenticator.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            firstNonce = request.Headers.GetValues("X-AIOffice-Group-Nonce").Single(); effects++;
            throw new IOException("PRIVATE_COMMITTED_BUT_REPLY_LOST");
        }))
        {
            var error = await Assert.ThrowsAsync<GroupConnectorTransportException>(() => fixture.Replay(spool, lost).ReplayAsync(reference, fixture.Enrollment, fixture.Lease));
            Assert.DoesNotContain("PRIVATE", error.ToString());
            Assert.Equal(bytes, fixture.Bytes()); Assert.Single(spool.Pending());
        }
        spool.Dispose();
        using var reopened = fixture.Open(); fixture.Clock.Current = Fixture.Now.AddSeconds(1);
        var newLease = fixture.Lease with { OwnerId = Guid.NewGuid(), Epoch = 2, HeartbeatAtUtc = fixture.Clock.Current, ExpiresAtUtc = fixture.Clock.Current.AddSeconds(30) };
        using var recovered = fixture.Client(async (request, token) =>
        {
            var payload = GroupServiceAuthenticator.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(committedPayload!.Event, payload.Event); Assert.Equal(committedPayload.Text, payload.Text);
            Assert.Equal(newLease.OwnerId, payload.ListenerOwnerId); Assert.Equal(2, payload.ListenerEpoch);
            Assert.NotEqual(firstNonce, request.Headers.GetValues("X-AIOffice-Group-Nonce").Single());
            return fixture.Reply(request, fixture.Receipt with { WasAlreadyCommitted = true });
        });
        var result = await fixture.Replay(reopened, recovered).ReplayAsync(Assert.Single(reopened.Pending()), fixture.Enrollment, newLease);
        Assert.Equal(fixture.Receipt with { WasAlreadyCommitted = true }, result); Assert.Equal(1, effects);
        Assert.Empty(reopened.Pending()); Assert.False(File.Exists(retainedPath));
        Assert.Equal(2, fixture.Keys.Calls);
    }

    [Theory]
    [InlineData("503")]
    [InlineData("403")]
    [InlineData("foreign")]
    [InlineData("future")]
    [InlineData("malformed")]
    [InlineData("io")]
    public async Task UnconfirmedCommitPreservesEveryRetainedByte(string fault)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var reference = fixture.Append(spool); var original = fixture.Bytes();
        using var client = fixture.Client((request, _) =>
        {
            if (fault == "io") throw new IOException("PRIVATE_REPLY_IO");
            var receipt = fixture.Receipt;
            if (fault == "foreign") receipt = receipt with { Source = receipt.Source with { SourceBindingId = Guid.NewGuid() } };
            if (fault == "future") receipt = receipt with { CommittedAtUtc = DateTimeOffset.MaxValue.ToUniversalTime() };
            var reply = fixture.Reply(request, receipt);
            if (int.TryParse(fault, out var status)) reply.StatusCode = (HttpStatusCode)status;
            if (fault == "malformed") { reply.Content.Dispose(); reply.Content = new StringContent("{}", Encoding.UTF8, "application/json"); }
            return Task.FromResult(reply);
        });
        var error = await Assert.ThrowsAsync<GroupConnectorTransportException>(() => fixture.Replay(spool, client).ReplayAsync(reference, fixture.Enrollment, fixture.Lease));
        Assert.DoesNotContain("PRIVATE", error.ToString()); Assert.Equal(original, fixture.Bytes()); Assert.Single(spool.Pending());
    }

    [Theory]
    [InlineData("source-version")]
    [InlineData("grant-version")]
    [InlineData("deletion")]
    [InlineData("grant-revoked")]
    [InlineData("service-revoked")]
    [InlineData("credential")]
    [InlineData("expired")]
    [InlineData("key-id")]
    [InlineData("live-fixture")]
    [InlineData("optional")]
    public async Task CurrentAuthorityAndHostBindingAreRequiredBeforeSpoolKeyResolution(string change)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        var reference = fixture.Append(spool, change == "optional" ? GroupSourceEventKind.Edit : GroupSourceEventKind.NewText); var bytes = fixture.Bytes();
        var current = fixture.Enrollment; var lease = fixture.Lease;
        if (change == "source-version") current = current with { Source = current.Source with { Version = 2 } };
        if (change == "grant-version") current = current with { Grant = current.Grant with { Version = 2 } };
        if (change == "deletion") current = current with { Source = current.Source with { DeletionGeneration = 1 } };
        if (change == "grant-revoked") current = current with { Grant = current.Grant with { IsEnabled = false } };
        if (change == "service-revoked") current = current with { Principal = current.Principal with { IsEnabled = false } };
        if (change == "credential") current = current with { Authentication = current.Authentication with { CredentialEpoch = 2 } };
        if (change == "expired") lease = lease with { ExpiresAtUtc = Fixture.Now };
        if (change == "key-id") reference = new(reference.Context with { KeyId = "untrusted-key-selector" });
        if (change == "optional") current = current with
        {
            Qualification = new(current.Source.Scope.TenantId, current.Source.Scope.CompanyId,
            current.Source.ConnectorAccountId, current.Source.ExternalIdentity.AccountId, current.Artifact, GroupQualificationEnvironment.Synthetic, [])
        };
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("HTTP must not execute"), live: change == "live-fixture");
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => fixture.Replay(spool, client).ReplayAsync(reference, current, lease));
        Assert.Equal(0, fixture.Keys.Calls); Assert.Equal(0, fixture.Auth.Secrets.Calls);
        Assert.Equal(bytes, fixture.Bytes()); Assert.Single(spool.Pending());
    }

    [Fact]
    public async Task LeaseExpiryDuringSpoolKeyAwaitRefusesBeforeSigningAndPreservesCiphertext()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var reference = fixture.Append(spool); var bytes = fixture.Bytes();
        fixture.Keys.BeforeResolution = () => fixture.Clock.Current = Fixture.Now.AddSeconds(31);
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("HTTP must not execute"));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => fixture.Replay(spool, client).ReplayAsync(reference, fixture.Enrollment, fixture.Lease));
        Assert.Equal(1, fixture.Keys.Calls); Assert.Equal(0, fixture.Auth.Secrets.Calls); Assert.Equal(bytes, fixture.Bytes());
    }

    [Fact]
    public async Task CiphertextTamperCannotSignSendOrDeleteTheRetainedFile()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var reference = fixture.Append(spool);
        var bytes = fixture.Bytes(); bytes[^1] ^= 1; File.WriteAllBytes(fixture.ItemPath, bytes);
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("HTTP must not execute"));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => fixture.Replay(spool, client).ReplayAsync(reference, fixture.Enrollment, fixture.Lease));
        Assert.Equal(1, fixture.Keys.Calls); Assert.Equal(0, fixture.Auth.Secrets.Calls); Assert.Equal(bytes, fixture.Bytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeReplayDeadlineAndCallerCancellationRetainBytesDuringNoncooperativeKeyResolution(bool caller)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var reference = fixture.Append(spool); var bytes = fixture.Bytes();
        var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Keys.Resolve = () => { started.TrySetResult(); return new(held.Task); };
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("HTTP must not execute"));
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Replay(spool, client).ReplayAsync(reference, fixture.Enrollment, fixture.Lease, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (caller) cancellation.Cancel(); else fixture.Clock.FireOuterDeadline();
        if (caller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        else await Assert.ThrowsAsync<GroupConnectorTransportException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        held.SetResult(Convert.ToBase64String(fixture.Keys.Key));
        Assert.Equal(bytes, fixture.Bytes()); Assert.Single(spool.Pending()); Assert.Equal(0, fixture.Auth.Secrets.Calls);
    }

    [Fact]
    public async Task CanceledUnknownHttpCannotDeleteBacklogWhenItsValidReplyArrivesLate()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var reference = fixture.Append(spool); var bytes = fixture.Bytes();
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = fixture.Client((request, _) => { started.TrySetResult(request); return held.Task; });
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Replay(spool, client).ReplayAsync(reference, fixture.Enrollment, fixture.Lease, cancellation.Token);
        var request = await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        held.SetResult(fixture.Reply(request, fixture.Receipt));
        Assert.Equal(bytes, fixture.Bytes()); Assert.Single(spool.Pending());
    }

    [Fact]
    public async Task OlderInFlightCommitCannotEraseARecapturedFileWithTheSameLogicalEvent()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var reference = fixture.Append(spool);
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = fixture.Client((request, _) => { started.TrySetResult(request); return held.Task; });
        var pending = fixture.Replay(spool, client).ReplayAsync(reference, fixture.Enrollment, fixture.Lease);
        var request = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(spool.Acknowledge(reference, fixture.Receipt));
        fixture.Clock.Current = Fixture.Now.AddSeconds(1); var newer = fixture.Append(spool); var bytes = fixture.Bytes();
        held.SetResult(fixture.Reply(request, fixture.Receipt));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => pending);
        Assert.Equal(bytes, fixture.Bytes()); Assert.Equal(newer.Context, Assert.Single(spool.Pending()).Context);
    }

    [Theory]
    [InlineData("signing-key")]
    [InlineData("foreign-company")]
    public void SpoolKeyConfigurationCannotReuseSigningReferenceOrAnotherCompany(string change)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("HTTP must not execute"));
        var binding = fixture.KeyBinding;
        if (change == "signing-key") binding = binding with { Reference = SecretReference.Parse("secretref://env/OWNED_GROUP_KEY") };
        if (change == "foreign-company") binding = binding with { CompanyId = Guid.NewGuid() };
        Assert.Throws<GroupConnectorTransportException>(() => new GroupConnectorSpoolTransport(spool, client, binding, new([fixture.Keys]), fixture.Clock));
        Assert.Equal(0, fixture.Keys.Calls); Assert.Empty(spool.Pending());
    }

    private sealed class Fixture : IDisposable
    {
        internal static DateTimeOffset Now => GroupServiceAuthenticatorTests.Fixture.Now;
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        internal readonly ReplayClock Clock = new();
        internal readonly SpoolSecrets Keys = new();
        private readonly string root = Path.Combine(Path.GetTempPath(), "aioffice-owned-replay-" + Guid.NewGuid().ToString("N"));
        private readonly Guid messageId = Guid.NewGuid();
        internal Fixture() { Directory.CreateDirectory(root); }
        internal GroupListenerAccountScope Account => new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id);
        internal GroupListenerLeaseSnapshot Lease => new(Account, Auth.Payload().ListenerOwnerId, 1, Now, Now.AddSeconds(30));
        internal GroupConnectorSpoolKeyBinding KeyBinding => new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id, Auth.Service.Id,
            "spool-v1", SecretReference.Parse("secretref://env/OWNED_SPOOL_KEY"));
        internal GroupConnectorEnrollment Enrollment
        {
            get
            {
                var artifact = new GroupConnectorArtifact(Auth.External.Provider, Auth.Account.PackageVersion, Auth.Account.GitCommit);
                return new(new(Auth.Service.Id, 1), new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, 1, true),
                    new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, Auth.Scope.SourceBindingId, GroupServiceCapability.Ingest, 1, true),
                    new(Auth.Scope, Auth.Account.Id, Auth.External, "Owned replay", 1, 0, true),
                    new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id, Auth.External.AccountId, artifact, GroupQualificationEnvironment.Synthetic,
                        [new(GroupConnectorCapability.EditEvents, GroupConnectorSupport.Supported, Guid.NewGuid(), Now)]), artifact);
            }
        }
        internal GroupIngressCommittedReceipt Receipt => new(Auth.Scope, messageId, 1, 1, Now, false);
        internal GroupConnectorFileSpool Open() => GroupConnectorFileSpool.Open(root, Account, Auth.Service.Id);
        internal GroupSpoolItemReference Append(GroupConnectorFileSpool spool, GroupSourceEventKind kind = GroupSourceEventKind.NewText)
        {
            var payload = Auth.Payload(); payload = payload with { Event = payload.Event with { Kind = kind } };
            var admitted = GroupConnectorSpoolAdmission.Filter(Enrollment, payload, Lease, Clock.Current, GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true));
            return spool.Append(new GroupSpoolContentProtector().Protect(admitted, Keys.Key, "spool-v1"));
        }
        internal string ItemPath => Directory.GetFiles(root, "*.spool", SearchOption.AllDirectories).Single();
        internal byte[] Bytes() => File.ReadAllBytes(ItemPath);
        internal GroupConnectorTransportClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply, bool live = false) =>
            new(new(live ? "https://owned.invalid/" : "http://127.0.0.1/"),
                new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, 1, SecretReference.Parse("secretref://env/OWNED_GROUP_KEY")),
                new([Auth.Secrets]), Clock, live ? GroupIngressRuntimePolicy.Live : GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), new Handler(reply));
        internal GroupConnectorSpoolTransport Replay(GroupConnectorFileSpool spool, GroupConnectorTransportClient client) => new(spool, client, KeyBinding, new([Keys]), Clock);
        internal HttpResponseMessage Reply(HttpRequestMessage request, object receipt) => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(JsonSerializer.Serialize(receipt, GroupServiceAuthenticator.JsonOptions), Encoding.UTF8, "application/json"),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Keys.Key); Auth.Dispose();
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Path.GetFileName(root)));
            if (Path.GetFullPath(root) != expected || !Path.GetFileName(root).StartsWith("aioffice-owned-replay-", StringComparison.Ordinal))
                throw new InvalidOperationException("Owned fixture cleanup boundary is invalid.");
            Directory.Delete(root, recursive: true);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken); }
    private sealed class SpoolSecrets : ISecretResolver
    {
        internal readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        internal int Calls;
        internal Action? BeforeResolution;
        internal Func<ValueTask<string>>? Resolve;
        public string Provider => "env";
        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            Assert.Equal("secretref://env/OWNED_SPOOL_KEY", reference.Value); Calls++; BeforeResolution?.Invoke();
            return Resolve?.Invoke() ?? ValueTask.FromResult(Convert.ToBase64String(Key));
        }
    }
    private sealed class ReplayClock : TimeProvider
    {
        internal DateTimeOffset Current = Fixture.Now;
        private readonly List<ControlledTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => Current;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ControlledTimer(callback, state); timers.Add(timer); return timer; }
        internal void FireOuterDeadline() => timers.First(x => !x.Disposed).Fire();
        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed;
            internal void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
