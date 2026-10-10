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
    [InlineData("signing-alias")]
    [InlineData("foreign-company")]
    public void SpoolKeyConfigurationCannotReuseSigningReferenceOrAnotherCompany(string change)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("HTTP must not execute"));
        var binding = fixture.KeyBinding;
        if (change == "signing-key") binding = binding with { Reference = SecretReference.Parse("secretref://env/OWNED_GROUP_KEY") };
        if (change == "signing-alias") binding = binding with { Reference = SecretReference.Parse("secretref://ENV/owned_group_key") };
        if (change == "foreign-company") binding = binding with { CompanyId = Guid.NewGuid() };
        Assert.Throws<GroupConnectorTransportException>(() => new GroupConnectorSpoolTransport(spool, client, binding, new([fixture.Keys]), fixture.Clock));
        Assert.Equal(0, fixture.Keys.Calls); Assert.Empty(spool.Pending());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("metadata403")]
    [InlineData("renew403")]
    [InlineData("source-version")]
    [InlineData("grant-version")]
    [InlineData("deletion")]
    [InlineData("metadata-age-after-key")]
    [InlineData("clock-back-after-key")]
    public async Task OperationalReplayFetchesCurrentBackendAndRenewsActualOwnershipBeforeSpoolKey(string outcome)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        var reference = fixture.Append(spool); var bytes = fixture.Bytes(); var calls = new List<string>();
        var enrollment = fixture.Enrollment;
        var snapshot = new GroupConnectorEnrollmentSnapshot(enrollment.Authentication, enrollment.Principal, enrollment.Grant,
            enrollment.Source, enrollment.Artifact, enrollment.Qualification.Environment, enrollment.Qualification.Observations, 1, Fixture.Now);
        if (outcome == "source-version") snapshot = snapshot with { Source = snapshot.Source with { Version = 2 } };
        if (outcome == "grant-version") snapshot = snapshot with { Grant = snapshot.Grant with { Version = 2 } };
        if (outcome == "deletion") snapshot = snapshot with { Source = snapshot.Source with { DeletionGeneration = 1 } };
        if (outcome == "metadata-age-after-key") fixture.Keys.BeforeResolution = () => fixture.Clock.Current = Fixture.Now.AddSeconds(11);
        if (outcome == "clock-back-after-key") fixture.Keys.BeforeResolution = () => fixture.Clock.Current = Fixture.Now.AddTicks(-1);
        using var client = fixture.Client(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath; calls.Add(path);
            if (path.EndsWith("/enrollment", StringComparison.Ordinal))
            {
                Assert.Equal(0, fixture.Keys.Calls);
                var reply = fixture.Reply(request, snapshot);
                if (outcome == "metadata403") reply.StatusCode = HttpStatusCode.Forbidden;
                return reply;
            }
            if (path.EndsWith("/listener", StringComparison.Ordinal))
            {
                Assert.Equal(0, fixture.Keys.Calls);
                var command = GroupServiceAuthenticator.ParseListener(await request.Content!.ReadAsByteArrayAsync(token)).Command;
                Assert.Equal(new GroupListenerCommand(fixture.Lease.OwnerId, GroupListenerOperation.Renew, fixture.Lease.Epoch), command);
                var reply = fixture.Reply(request, new GroupListenerCommittedReceipt(fixture.Lease, true, false, Fixture.Now, false));
                if (outcome == "renew403") reply.StatusCode = HttpStatusCode.Forbidden;
                return reply;
            }
            Assert.Equal("/internal/group-ingress/events", path); Assert.Equal(1, fixture.Keys.Calls);
            return fixture.Reply(request, fixture.Receipt);
        });
        var operation = fixture.Replay(spool, client).ReplayWithCurrentAuthorityAsync(reference, new(fixture.Auth.Scope, fixture.Auth.External), fixture.Lease);
        if (outcome == "success")
        {
            Assert.Equal(fixture.Receipt, await operation); Assert.Empty(spool.Pending());
            Assert.Equal(new[] { "/internal/group-ingress/enrollment", "/internal/group-ingress/listener", "/internal/group-ingress/events" }, calls);
        }
        else
        {
            await Assert.ThrowsAsync<GroupConnectorTransportException>(() => operation);
            Assert.Equal(bytes, fixture.Bytes()); Assert.Single(spool.Pending());
            var afterKey = outcome is "metadata-age-after-key" or "clock-back-after-key";
            Assert.Equal(afterKey ? 1 : 0, fixture.Keys.Calls);
            Assert.Equal(outcome == "renew403" || afterKey ? 2 : 1, calls.Count);
        }
    }

    [Fact]
    public async Task RecoverySessionRejectsDirectSelfEchoAndUnenrolledTrafficBeforeKeysOrHttp()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("Private HTTP must not execute"));
        var sources = new[] { new GroupConnectorEnrollmentRequest(fixture.Auth.Scope, fixture.Auth.External) };
        var session = fixture.Session(spool, client, sources);
        sources[0] = sources[0] with { Identity = fixture.Auth.External with { GroupId = "another-group" } };
        var payload = fixture.Auth.Payload();
        foreach (var refused in new[] { payload with { IsGroup = false }, payload with { IsSelf = true }, payload with { IsKnownReportEcho = true },
            payload with { Event = payload.Event with { Identity = fixture.Auth.External with { GroupId = "another-group" } } } })
        {
            await Assert.ThrowsAsync<GroupConnectorTransportException>(() => session.CaptureAsync(refused));
            Assert.Equal(0, fixture.Keys.Calls); Assert.Empty(spool.Pending());
        }
    }

    [Fact]
    public async Task RecoverySessionUnknownCommitRetainsCaptureThenAutomaticallyReconcilesSameOwnerAndEvent()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        var lost = true;
        using var client = fixture.Backend(commands, events, _ => { if (!lost) return false; lost = false; return true; });
        var session = fixture.Session(spool, client);
        var error = await Assert.ThrowsAsync<GroupConnectorTransportException>(() => session.CaptureAsync(fixture.Auth.Payload()));
        Assert.DoesNotContain("PRIVATE", error.ToString());
        var retained = Assert.Single(spool.Pending()); var bytes = fixture.Bytes();
        Assert.Equal(fixture.Auth.Scope, retained.Context.Source);
        Assert.Equal(new[] { GroupListenerOperation.Acquire, GroupListenerOperation.Renew }, commands.Select(command => command.Operation));
        Assert.NotEqual(fixture.Lease.OwnerId, commands[0].OwnerId);
        Assert.Equal(new GroupConnectorRecoveryPass(1, 0, 0), await session.RecoverOnceAsync());
        Assert.Equal(new[] { GroupListenerOperation.Acquire, GroupListenerOperation.Renew, GroupListenerOperation.Acquire, GroupListenerOperation.Renew },
            commands.Select(command => command.Operation));
        Assert.Single(commands.Select(command => command.OwnerId).Distinct());
        Assert.Equal(2, events.Count); Assert.Equal(events[0], events[1]); Assert.Empty(spool.Pending());
        Assert.True(bytes.Length > 30);
    }

    [Fact]
    public async Task RecoverySessionFairBoundedPassRetainsPoisonWithoutStarvingLaterCapture()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        for (var index = 0; index < 33; index++) fixture.Append(spool, revisionEventId: "owned-capture-" + index);
        var ordered = spool.Pending().OrderBy(item => item.Context.EventIdentityHash, StringComparer.Ordinal).ToArray();
        var poison = ordered[0]; var poisonBytes = spool.Load(poison).Envelope.ToArray();
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events, payload =>
            GroupIngressIdentity.EventIndex(fixture.Auth.Scope, payload.Event.RevisionEventId) == poison.Context.EventIdentityHash);
        var session = fixture.Session(spool, client);
        Assert.Equal(new GroupConnectorRecoveryPass(31, 1, 2), await session.RecoverOnceAsync());
        Assert.Equal(poisonBytes, spool.Load(poison).Envelope);
        Assert.Equal(new GroupConnectorRecoveryPass(1, 0, 1), await session.RecoverOnceAsync());
        Assert.Equal(poison.Context, Assert.Single(spool.Pending()).Context);
        Assert.Equal(new GroupConnectorRecoveryPass(0, 1, 1), await session.RecoverOnceAsync());
        Assert.Equal(poisonBytes, spool.Load(poison).Envelope);
        Assert.Equal(32, events.Select(item => item.Event.RevisionEventId).Distinct().Count(item =>
            GroupIngressIdentity.EventIndex(fixture.Auth.Scope, item) != poison.Context.EventIdentityHash));
        Assert.Single(commands.Select(command => command.OwnerId).Distinct());
    }

    [Fact]
    public async Task RecoverySessionReauthorizesBeforeCaptureKeyAndExpiryPreservesEmptySpool()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        using (var denied = fixture.Client((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request })))
        {
            await Assert.ThrowsAsync<GroupConnectorTransportException>(() => fixture.Session(spool, denied).CaptureAsync(fixture.Auth.Payload()));
            Assert.Equal(0, fixture.Keys.Calls); Assert.Empty(spool.Pending());
        }
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events);
        fixture.Keys.BeforeResolution = () => fixture.Clock.Current = Fixture.Now.AddSeconds(31);
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => fixture.Session(spool, client).CaptureAsync(fixture.Auth.Payload()));
        Assert.Equal(1, fixture.Keys.Calls); Assert.Empty(events); Assert.Empty(spool.Pending());
        Assert.Equal(GroupListenerOperation.Acquire, Assert.Single(commands).Operation);
    }

    [Fact]
    public async Task RecoverySessionEmptyHeartbeatDoesNotClaimConnectionOrReadSpoolKey()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events);
        var session = fixture.Session(spool, client);
        Assert.Equal(new GroupConnectorRecoveryPass(0, 0, 0), await session.RecoverOnceAsync());
        fixture.Clock.Current = Fixture.Now.AddSeconds(20);
        Assert.Equal(new GroupConnectorRecoveryPass(0, 0, 0), await session.RecoverOnceAsync());
        Assert.Equal(new[] { GroupListenerOperation.Acquire, GroupListenerOperation.Renew }, commands.Select(command => command.Operation));
        Assert.Equal(0, fixture.Keys.Calls); Assert.Empty(events); Assert.Empty(spool.Pending());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("account")]
    [InlineData("service")]
    [InlineData("disposed")]
    public void RecoverySessionRequiresExactOpenSpoolOwnershipBeforeKeysOrHttp(string changed)
    {
        using var fixture = new Fixture(); var account = fixture.Account;
        if (changed == "tenant") account = account with { TenantId = Guid.NewGuid() };
        if (changed == "company") account = account with { CompanyId = Guid.NewGuid() };
        if (changed == "account") account = account with { ConnectorAccountId = Guid.NewGuid() };
        using var spool = fixture.Open(account, changed == "service" ? Guid.NewGuid() : fixture.Auth.Service.Id);
        if (changed == "disposed") spool.Dispose();
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("HTTP must not execute"));
        Assert.Throws<GroupConnectorTransportException>(() => fixture.Session(spool, client));
        Assert.Throws<GroupConnectorTransportException>(() => fixture.Replay(spool, client));
        Assert.Equal(0, fixture.Keys.Calls); Assert.Equal(0, fixture.Auth.Secrets.Calls);
    }

    [Theory]
    [InlineData("duplicate-source")]
    [InlineData("duplicate-identity")]
    [InlineData("foreign-company")]
    [InlineData("foreign-account")]
    [InlineData("foreign-provider")]
    [InlineData("empty")]
    [InlineData("overflow")]
    public void RecoverySessionRejectsUntrustedEnrollmentConfigurationWithoutResources(string changed)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        var original = new GroupConnectorEnrollmentRequest(fixture.Auth.Scope, fixture.Auth.External);
        var other = original with
        {
            Source = original.Source with { SourceBindingId = Guid.NewGuid() },
            Identity = original.Identity with { GroupId = "owned-other-group" }
        };
        if (changed == "duplicate-source") other = other with { Source = original.Source };
        if (changed == "duplicate-identity") other = other with { Identity = original.Identity };
        if (changed == "foreign-company") other = other with { Source = other.Source with { CompanyId = Guid.NewGuid() } };
        if (changed == "foreign-account") other = other with { Identity = other.Identity with { AccountId = "owned-other-account" } };
        if (changed == "foreign-provider") other = other with { Identity = other.Identity with { Provider = "other" } };
        GroupConnectorEnrollmentRequest[] configured = changed == "empty" ? [] : changed == "overflow" ? Enumerable.Repeat(original, 257).ToArray() : [original, other];
        using var client = fixture.Client((_, _) => throw new InvalidOperationException("HTTP must not execute"));
        Assert.Throws<GroupConnectorTransportException>(() => fixture.Session(spool, client, configured));
        Assert.Equal(0, fixture.Keys.Calls); Assert.Equal(0, fixture.Auth.Secrets.Calls); Assert.Empty(spool.Pending());
    }

    [Fact]
    public async Task CanceledWaitingRecoveryCannotDiscardLeaseOfRunningCapture()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Keys.Resolve = () => { started.TrySetResult(); return new(release.Task); };
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events); var session = fixture.Session(spool, client);
        var capture = session.CaptureAsync(fixture.Auth.Payload());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var waiting = session.RecoverOnceAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(GroupListenerOperation.Acquire, Assert.Single(commands).Operation);
        release.SetResult(Convert.ToBase64String(fixture.Keys.Key));
        Assert.Equal(fixture.Receipt, await capture.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { GroupListenerOperation.Acquire, GroupListenerOperation.Renew }, commands.Select(command => command.Operation));
        Assert.Single(commands.Select(command => command.OwnerId).Distinct()); Assert.Single(events); Assert.Empty(spool.Pending());
    }

    [Fact]
    public async Task CancellationAfterKeyDecodeStillRefusesBeforeLocalAppend()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); using var cancellation = new CancellationTokenSource();
        fixture.Keys.BeforeResolution = () => fixture.Clock.BeforeRead = cancellation.Cancel;
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Session(spool, client).CaptureAsync(fixture.Auth.Payload(), cancellation.Token));
        Assert.Equal(1, fixture.Keys.Calls); Assert.Empty(spool.Pending()); Assert.Empty(events);
        Assert.Equal(GroupListenerOperation.Acquire, Assert.Single(commands).Operation);
    }

    [Theory]
    [InlineData("source-version")]
    [InlineData("grant-version")]
    [InlineData("deletion")]
    [InlineData("credential")]
    [InlineData("grant-revoked")]
    [InlineData("service-revoked")]
    public async Task RecoverySessionCurrentAuthorityCannotDecryptAnObsoleteRetainedCapture(string changed)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var reference = fixture.Append(spool); var bytes = fixture.Bytes();
        var enrollment = fixture.Enrollment;
        if (changed == "source-version") enrollment = enrollment with { Source = enrollment.Source with { Version = 2 } };
        if (changed == "grant-version") enrollment = enrollment with { Grant = enrollment.Grant with { Version = 2 } };
        if (changed == "deletion") enrollment = enrollment with { Source = enrollment.Source with { DeletionGeneration = 1 } };
        if (changed == "credential") enrollment = enrollment with { Authentication = enrollment.Authentication with { CredentialEpoch = 2 } };
        if (changed == "grant-revoked") enrollment = enrollment with { Grant = enrollment.Grant with { IsEnabled = false } };
        if (changed == "service-revoked") enrollment = enrollment with { Principal = enrollment.Principal with { IsEnabled = false } };
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events, enrollment: enrollment);
        Assert.Equal(new GroupConnectorRecoveryPass(0, 1, 1), await fixture.Session(spool, client).RecoverOnceAsync());
        Assert.Equal(0, fixture.Keys.Calls); Assert.Empty(events); Assert.Equal(bytes, fixture.Bytes());
        Assert.Equal(reference.Context, Assert.Single(spool.Pending()).Context);
    }

    [Fact]
    public async Task RestartedSessionWaitsForOldLeaseExpiryThenReplaysSameEventUnderNewEpoch()
    {
        using var fixture = new Fixture(); var spool = fixture.Open();
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events, _ => events.Count == 1);
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => fixture.Session(spool, client).CaptureAsync(fixture.Auth.Payload()));
        var reference = Assert.Single(spool.Pending()); var bytes = fixture.Bytes(); var keyCalls = fixture.Keys.Calls;
        spool.Dispose(); using var reopened = fixture.Open(); var restarted = fixture.Session(reopened, client);
        Assert.Equal(new GroupConnectorRecoveryPass(0, 1, 1), await restarted.RecoverOnceAsync());
        Assert.Equal(bytes, fixture.Bytes()); Assert.Equal(keyCalls, fixture.Keys.Calls);
        fixture.Clock.Current = Fixture.Now.AddSeconds(31);
        Assert.Equal(new GroupConnectorRecoveryPass(1, 0, 0), await restarted.RecoverOnceAsync());
        Assert.Equal(2, events.Count); Assert.Equal(events[0].Event, events[1].Event); Assert.Equal(events[0].Text, events[1].Text);
        Assert.Equal(1, events[0].ListenerEpoch); Assert.Equal(2, events[1].ListenerEpoch);
        Assert.NotEqual(events[0].ListenerOwnerId, events[1].ListenerOwnerId);
        Assert.Equal(reference.Context.Source, fixture.Receipt.Source); Assert.Empty(reopened.Pending());
    }

    [Theory]
    [InlineData("metadata", false)]
    [InlineData("metadata", true)]
    [InlineData("listener", false)]
    [InlineData("listener", true)]
    [InlineData("key", false)]
    [InlineData("key", true)]
    [InlineData("event", false)]
    [InlineData("event", true)]
    public async Task RecoverySessionBoundsNoncooperativeCaptureAndOwnsLateReplies(string phase, bool caller)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (phase == "key") fixture.Keys.Resolve = () => { started.TrySetResult(); return new(keyRelease.Task); };
        HttpRequestMessage? heldRequest = null;
        var commands = new List<GroupListenerCommand>(); var events = new List<GroupIngressPayload>();
        using var client = fixture.Backend(commands, events, intercept: (request, _) =>
        {
            var target = phase == "metadata" ? "/enrollment" : phase == "listener" ? "/listener" : phase == "event" ? "/events" : "/unreachable";
            if (!request.RequestUri!.AbsolutePath.EndsWith(target, StringComparison.Ordinal)) return null;
            heldRequest = request; started.TrySetResult(); return release.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var capture = fixture.Session(spool, client).CaptureAsync(fixture.Auth.Payload(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var retained = phase == "event" ? fixture.Bytes() : null;
        if (caller) cancellation.Cancel(); else fixture.Clock.FireOuterDeadline();
        if (caller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TimeSpan.FromSeconds(2)));
        else
        {
            var error = await Assert.ThrowsAsync<GroupConnectorTransportException>(() => capture.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.DoesNotContain("PRIVATE", error.ToString());
        }
        if (phase == "key") keyRelease.SetResult(Convert.ToBase64String(fixture.Keys.Key));
        else if (phase == "event") release.SetResult(fixture.Reply(heldRequest!, fixture.Receipt));
        else release.SetException(new IOException("PRIVATE_LATE_REPLY"));
        Assert.Empty(events);
        if (retained is not null) { Assert.Single(spool.Pending()); Assert.Equal(retained, fixture.Bytes()); }
        else Assert.Empty(spool.Pending());
        Assert.Equal(phase is "key" or "event" ? phase == "event" ? 2 : 1 : 0, fixture.Keys.Calls);
    }

    [Fact]
    public async Task RecoveryHostRetainsActualRenewedLeaseAcrossOriginalSnapshotExpiry()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open();
        var first = fixture.Append(spool); var second = fixture.Append(spool, revisionEventId: "owned-second-capture");
        fixture.Clock.Current = Fixture.Now.AddSeconds(20);
        var enrollment = fixture.Enrollment;
        var calls = new List<string>();
        using var client = fixture.Client(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath; calls.Add(path);
            if (path.EndsWith("/enrollment", StringComparison.Ordinal)) return fixture.Reply(request,
                new GroupConnectorEnrollmentSnapshot(enrollment.Authentication, enrollment.Principal, enrollment.Grant,
                    enrollment.Source, enrollment.Artifact, enrollment.Qualification.Environment, enrollment.Qualification.Observations, 1, fixture.Clock.Current));
            if (path.EndsWith("/listener", StringComparison.Ordinal))
            {
                var command = GroupServiceAuthenticator.ParseListener(await request.Content!.ReadAsByteArrayAsync(token)).Command;
                Assert.Equal(new GroupListenerCommand(fixture.Lease.OwnerId, GroupListenerOperation.Renew, 1), command);
                var actual = fixture.Lease with { HeartbeatAtUtc = fixture.Clock.Current, ExpiresAtUtc = fixture.Clock.Current.AddSeconds(30) };
                return fixture.Reply(request, new GroupListenerCommittedReceipt(actual, true, false, fixture.Clock.Current, false));
            }
            Assert.Equal("/internal/group-ingress/events", path);
            return fixture.Reply(request, fixture.Receipt with { CommittedAtUtc = fixture.Clock.Current });
        });
        var transport = fixture.Replay(spool, client);
        var selector = new GroupConnectorEnrollmentRequest(fixture.Auth.Scope, fixture.Auth.External);
        var committed = await transport.ReplayWithCurrentAuthorityAndLeaseAsync(first, selector, fixture.Lease);
        Assert.Equal(Fixture.Now.AddSeconds(50), committed.Lease.ExpiresAtUtc);
        Assert.Equal(Fixture.Now.AddSeconds(20), committed.Lease.HeartbeatAtUtc);
        Assert.Equal(fixture.Receipt with { CommittedAtUtc = fixture.Clock.Current }, committed.Receipt);
        Assert.Equal(second.Context, Assert.Single(spool.Pending()).Context);

        fixture.Clock.Current = Fixture.Now.AddSeconds(31);
        var retained = fixture.Bytes(); var keyCalls = fixture.Keys.Calls; var before = calls.Count;
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() =>
            transport.ReplayWithCurrentAuthorityAndLeaseAsync(second, selector, fixture.Lease));
        Assert.Equal(retained, fixture.Bytes()); Assert.Equal(keyCalls, fixture.Keys.Calls);
        Assert.Equal(new[] { "/internal/group-ingress/enrollment" }, calls.Skip(before));
        var recovered = await transport.ReplayWithCurrentAuthorityAndLeaseAsync(second, selector, committed.Lease);
        Assert.Equal(Fixture.Now.AddSeconds(61), recovered.Lease.ExpiresAtUtc);
        Assert.Equal(fixture.Lease.Account, recovered.Lease.Account); Assert.Equal(fixture.Lease.OwnerId, recovered.Lease.OwnerId);
        Assert.Equal(fixture.Lease.Epoch, recovered.Lease.Epoch); Assert.Empty(spool.Pending());
        Assert.Equal(keyCalls + 1, fixture.Keys.Calls);
    }

    [Fact]
    public async Task OperationalOuterDeadlineCancelsNoncooperativeMetadataBeforeBacklogDecryption()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var reference = fixture.Append(spool); var bytes = fixture.Bytes();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = fixture.Client((_, _) => { entered.TrySetResult(); return release.Task; });
        var operation = fixture.Replay(spool, client).ReplayWithCurrentAuthorityAsync(reference, new(fixture.Auth.Scope, fixture.Auth.External), fixture.Lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); fixture.Clock.FireOuterDeadline();
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => operation);
        Assert.Equal(0, fixture.Keys.Calls); Assert.Equal(bytes, fixture.Bytes()); Assert.Single(spool.Pending());
        release.TrySetException(new IOException("PRIVATE_LATE_METADATA"));
    }

    internal sealed class Fixture : IDisposable
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
        internal GroupConnectorFileSpool Open(GroupListenerAccountScope? account = null, Guid? serviceId = null) =>
            GroupConnectorFileSpool.Open(root, account ?? Account, serviceId ?? Auth.Service.Id);
        internal GroupSpoolItemReference Append(GroupConnectorFileSpool spool, GroupSourceEventKind kind = GroupSourceEventKind.NewText, string? revisionEventId = null)
        {
            var payload = Auth.Payload(); payload = payload with { Event = payload.Event with { Kind = kind } };
            if (revisionEventId is not null) payload = payload with { Event = payload.Event with { RevisionEventId = revisionEventId } };
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
        internal GroupConnectorRecoverySession Session(GroupConnectorFileSpool spool, GroupConnectorTransportClient client,
            IReadOnlyList<GroupConnectorEnrollmentRequest>? sources = null) => new(spool, client, KeyBinding, new([Keys]), Clock,
                GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), sources ?? [new(Auth.Scope, Auth.External)]);
        internal GroupConnectorTransportClient Backend(List<GroupListenerCommand> commands, List<GroupIngressPayload> events,
            Func<GroupIngressPayload, bool>? loseReply = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>?>? intercept = null,
            GroupConnectorEnrollment? enrollment = null)
        {
            GroupListenerLeaseSnapshot? currentLease = null;
            enrollment ??= Enrollment;
            return Client(async (request, token) =>
            {
                if (intercept?.Invoke(request, token) is { } intercepted) return await intercepted;
                if (request.RequestUri!.AbsolutePath.EndsWith("/enrollment", StringComparison.Ordinal)) return Reply(request,
                    new GroupConnectorEnrollmentSnapshot(enrollment.Authentication, enrollment.Principal, enrollment.Grant,
                        enrollment.Source, enrollment.Artifact, enrollment.Qualification.Environment, enrollment.Qualification.Observations, 1, Clock.Current));
                if (request.RequestUri.AbsolutePath.EndsWith("/listener", StringComparison.Ordinal))
                {
                    var command = GroupServiceAuthenticator.ParseListener(await request.Content!.ReadAsByteArrayAsync(token)).Command;
                    commands.Add(command);
                    var transition = GroupListenerLeasePolicy.Apply(Account, command, currentLease, Clock.Current);
                    currentLease = transition.Lease;
                    return Reply(request, new GroupListenerCommittedReceipt(currentLease, transition.Changed,
                        transition.CoverageReason is not null, Clock.Current, !transition.Changed));
                }
                var payload = GroupServiceAuthenticator.Parse(await request.Content!.ReadAsByteArrayAsync(token)); events.Add(payload);
                Assert.Equal(currentLease!.OwnerId, payload.ListenerOwnerId); Assert.Equal(currentLease.Epoch, payload.ListenerEpoch);
                if (loseReply?.Invoke(payload) == true) throw new IOException("PRIVATE_COMMITTED_REPLY_LOST");
                return Reply(request, Receipt with { CommittedAtUtc = Clock.Current, WasAlreadyCommitted = events.Count > 1 });
            });
        }
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
    internal sealed class SpoolSecrets : ISecretResolver
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
    internal sealed class ReplayClock : TimeProvider
    {
        internal DateTimeOffset Current = Fixture.Now;
        internal Action? BeforeRead;
        private readonly List<ControlledTimer> timers = [];
        public override DateTimeOffset GetUtcNow()
        { var before = BeforeRead; BeforeRead = null; before?.Invoke(); return Current; }
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
