using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task IgnoredKeyCancellationCannotHoldReaderAndLateKeyIsDisposedWithoutContext()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Keys.BeforeRead = async () => { entered.SetResult(); await release.Task; };
        using var canceled = new CancellationTokenSource();
        var read = f.Reader.ReadAsync(claim, [message.MessageId], canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(f.Keys.LastMaterial); release.SetResult();
        await f.Keys.MaterialReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopped = false;
        for (var attempt = 0; attempt < 20 && !stopped; attempt++)
        {
            try { _ = f.Keys.LastMaterial!.Key.Length; }
            catch (ObjectDisposedException) { stopped = true; }
            if (!stopped) await Task.Delay(10);
        }
        Assert.True(stopped); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task SelectedIdsAreFrozenBeforeAwaitAndCancellationAndForeignWorkerFailBeforeDatabase()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var selected = new[] { message.MessageId };
        f.Keys.BeforeRead = () => { selected[0] = Guid.NewGuid(); return Task.CompletedTask; };
        Assert.Equal(message.MessageId, Assert.Single((await f.Reader.ReadAsync(claim, selected)).Items).MessageId);
        f.Auth.Db.Dispose();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Reader.ReadAsync(claim, [message.MessageId], canceled.Token));
        var foreign = new GroupBatchSourceReader(f.Auth.Db, f.Worker with { CompanyId = Guid.NewGuid() }, f.Auth.Clock, f.Keys, new());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.ReadAsync(claim, [message.MessageId]));
    }

    [Theory]
    [InlineData("key-id")]
    [InlineData("sender")]
    [InlineData("message")]
    [InlineData("receipt-time")]
    [InlineData("cipher-size")]
    public async Task MalformedStoredMaterialRefusesBeforeAnyKeyLookup(string corruption)
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var revision = await f.Auth.Db.GroupMessageRevisions.SingleAsync();
        switch (corruption)
        {
            case "key-id": revision.ContentKeyId = new string('a', 65); break;
            case "sender": revision.SenderId = "\ud800"; break;
            case "message": (await f.Auth.Db.GroupMessages.SingleAsync()).ExternalMessageId += "changed"; break;
            case "receipt-time": (await f.Auth.Db.GroupIngressReceipts.SingleAsync()).CommittedAtUtc += TimeSpan.FromTicks(1); break;
            case "cipher-size": revision.ProtectedContent = new byte[65537]; break;
        }
        await f.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, [message.MessageId]));
        Assert.Equal(0, f.Keys.Reads); Assert.Empty(await f.Auth.Db.Tasks.ToArrayAsync());
    }

    [Fact]
    public async Task MaximumOriginalUnicodeIsWholeAndAKeyIsResolvedOnceForMultipleSelectedMessages()
    {
        using var f = new Fixture(); var text = string.Concat(Enumerable.Repeat("😀", 4000));
        var first = await f.CommitAsync(f.Payload(text: text));
        var second = await f.CommitAsync(f.Payload(messageId: "second", eventId: "second-event", text: "other"));
        var claim = await f.ClaimAsync(); var context = await f.Reader.ReadAsync(claim, [first.MessageId, second.MessageId]);
        Assert.Equal(text, context.Items[0].Text); Assert.Equal(8000, context.Items[0].Text!.Length);
        Assert.Equal("other", context.Items[1].Text); Assert.Equal(1, f.Keys.Reads);
    }

    [Fact]
    public async Task TrustedExtractReadsExactOriginalWithNoPortalIdentityAndSafeFormatting()
    {
        using var f = new Fixture(); var payload = f.Payload("message😀� \uFEFF", "event😀 \uFEFF", "original private 😀\uFEFF ");
        payload = payload with { Event = payload.Event with { ReplyToMessageId = "reply😀 " } };
        var message = await f.CommitAsync(payload); var claim = await f.ClaimAsync(); var before = await f.CountsAsync();
        var context = await f.Reader.ReadAsync(claim, [message.MessageId]); var entry = Assert.Single(context.Items);
        Assert.Equal(payload.Text, entry.Text); Assert.Equal(payload.Event.MessageId, entry.ExternalMessageId);
        Assert.Equal(payload.Event.ReplyToMessageId, entry.ReplyToMessageId);
        Assert.Equal(GroupBatchSourceDisposition.Readable, entry.Disposition); Assert.Equal(1, f.Keys.Reads);
        Assert.False(context.HasCoverageGap); Assert.Equal(before, await f.CountsAsync());
        Assert.DoesNotContain(payload.Text, context.ToString()); Assert.DoesNotContain(payload.Text, entry.ToString());
        Assert.Empty(typeof(GroupBatchSourceContext).GetConstructors()); Assert.Empty(typeof(GroupBatchSourceEntry).GetConstructors());
        await f.Reader.RequireCurrentAsync(context); Assert.Equal(1, f.Keys.Reads);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("too-many")]
    public async Task OnlyBoundedDistinctAllocatedIdsCanBecomeContext(string variant)
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var claim = await f.ClaimAsync();
        Guid[] ids = variant switch
        {
            "foreign" => [Guid.NewGuid()],
            "duplicate" => [message.MessageId, message.MessageId],
            "empty" => [],
            _ => Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray()
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, ids)); Assert.Equal(0, f.Keys.Reads);
    }

    [Fact]
    public async Task RecallAndEditPrecedenceAreAsOfCutoffAndRecallNeverResolvesPrivateKeys()
    {
        using var f = new Fixture(); await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Edit, eventId: "first-edit", text: "edited"));
        var recalled = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Recall, eventId: "recall", text: ""));
        await f.CommitAsync(f.Payload(eventId: "late-new"));
        var claim = await f.ClaimAsync(); var context = await f.Reader.ReadAsync(claim, [recalled.MessageId]);
        var entry = Assert.Single(context.Items);
        Assert.Equal(recalled.Revision, entry.Revision); Assert.Equal(GroupBatchSourceDisposition.Recalled, entry.Disposition);
        Assert.Null(entry.Text); Assert.Equal(0, f.Keys.Reads);
    }

    [Fact]
    public async Task WinningHeadMayPredateAllocationIntervalAndUnrelatedCursorChangesDoNotInvalidateIt()
    {
        using var f = new Fixture(); var edit = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Edit, eventId: "original-edit", text: "edited"));
        await f.ClaimAsync();
        await f.CommitAsync(f.Payload(eventId: "late-new"));
        var secondClaim = await f.ClaimAsync();
        var context = await f.Reader.ReadAsync(secondClaim, [edit.MessageId]);
        var entry = Assert.Single(context.Items); Assert.Equal(edit.Revision, entry.Revision); Assert.Equal("edited", entry.Text);
        await f.CommitAsync(f.Payload(messageId: "unrelated", eventId: "unrelated-event"));
        await f.Reader.RequireCurrentAsync(context);
        Assert.Equal(1, f.Keys.Reads);
    }

    [Theory]
    [InlineData(GroupSourceEventKind.Edit)]
    [InlineData(GroupSourceEventKind.Recall)]
    public async Task LaterWinningHeadHasHonestDispositionAndNeverReleasesFrozenPrivateBody(GroupSourceEventKind kind)
    {
        using var f = new Fixture(); var original = await f.CommitAsync(); var claim = await f.ClaimAsync();
        await f.CommitAsync(f.Payload(eventId: "changed", kind: kind, text: kind == GroupSourceEventKind.Recall ? "" : "changed"));
        var context = await f.Reader.ReadAsync(claim, [original.MessageId]); var entry = Assert.Single(context.Items);
        Assert.Equal(original.Revision, entry.Revision); Assert.Equal(GroupBatchSourceDisposition.ChangedAfterCutoff, entry.Disposition);
        Assert.Null(entry.Text); Assert.Equal(0, f.Keys.Reads);
    }

    [Fact]
    public async Task OldGenerationIsMetadataOnlyUnderNewCurrentAuthority()
    {
        using var f = new Fixture(); var original = await f.CommitAsync();
        var allocated = await f.AllocateAsync(); f.Auth.Binding.DeletionGeneration++; f.Auth.Binding.Version++;
        await f.Auth.Db.SaveChangesAsync(); var claim = await f.ClaimAllocatedAsync(allocated.BatchId);
        var entry = Assert.Single((await f.Reader.ReadAsync(claim, [original.MessageId])).Items);
        Assert.Equal(GroupBatchSourceDisposition.ObsoleteGeneration, entry.Disposition); Assert.Null(entry.Text); Assert.Equal(0, f.Keys.Reads);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("source-version")]
    [InlineData("deletion")]
    [InlineData("service")]
    [InlineData("account")]
    public async Task CurrentAuthorityLossAfterKeyAwaitIsCheckedBeforeDecryption(string variant)
    {
        using var f = new Fixture(); var original = await f.CommitAsync(); var claim = await f.ClaimAsync();
        // Corrupt original cipher would fail decryption if fresh authority was
        // skipped. The expected refusal proves authority wins before decrypt.
        var source = await f.Auth.Db.GroupMessageRevisions.SingleAsync();
        source.ProtectedContent = source.ProtectedContent.ToArray(); source.ProtectedContent[^1] ^= 1;
        await f.Auth.Db.SaveChangesAsync();
        f.Keys.BeforeRead = async () =>
        {
            switch (variant)
            {
                case "grant": f.Extract.IsEnabled = false; break;
                case "source-version": f.Auth.Binding.Version++; break;
                case "deletion": f.Auth.Binding.DeletionGeneration++; break;
                case "service": f.Auth.Service.IsEnabled = false; break;
                case "account": f.Auth.Account.IsEnabled = false; break;
            }
            await f.Auth.Db.SaveChangesAsync();
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reader.ReadAsync(claim, [original.MessageId]));
        Assert.Equal(1, f.Keys.Reads);
    }

    [Fact]
    public async Task RecallDuringKeyAwaitInvalidatesOnlySelectedDependencies()
    {
        using var f = new Fixture(); var first = await f.CommitAsync();
        var unaffected = await f.CommitAsync(f.Payload(messageId: "other", eventId: "other-event")); var claim = await f.ClaimAsync();
        f.Keys.BeforeRead = async () => await f.CommitAsync(f.Payload(eventId: "late-recall", kind: GroupSourceEventKind.Recall, text: ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, [first.MessageId]));
        f.Keys.BeforeRead = null;
        var readable = Assert.Single((await f.Reader.ReadAsync(claim, [unaffected.MessageId])).Items);
        Assert.Equal(GroupBatchSourceDisposition.Readable, readable.Disposition); Assert.NotNull(readable.Text);
    }

    [Fact]
    public async Task ExpiryDuringKeyAwaitCommitsMetadataWitnessAndNeverReleasesContextAcrossRollback()
    {
        using var f = new Fixture(); var original = await f.CommitAsync(); var claim = await f.ClaimAsync();
        f.Keys.BeforeRead = () => { f.Auth.Clock.Current = claim.Receipt.ExpiresAtUtc; return Task.CompletedTask; };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reader.ReadAsync(claim, [original.MessageId]));
        Assert.Equal(claim.Receipt.ExpiresAtUtc, (await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        f.Auth.Clock.Current -= TimeSpan.FromTicks(1); f.Keys.BeforeRead = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reader.ReadAsync(claim, [original.MessageId]));
        Assert.Equal(1, f.Keys.Reads);
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("reply")]
    [InlineData("receipt")]
    [InlineData("cipher")]
    public async Task FullEnvelopeMetadataAndCipherIntegrityAreRequired(string corruption)
    {
        using var f = new Fixture(); var original = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var revision = await f.Auth.Db.GroupMessageRevisions.SingleAsync();
        if (corruption == "sender") revision.SenderId = "changed-sender";
        if (corruption == "reply") revision.ReplyToMessageId = "changed-reply";
        if (corruption == "receipt") (await f.Auth.Db.GroupIngressReceipts.SingleAsync()).EnvelopeSha256 = new string('0', 64);
        if (corruption == "cipher") { revision.ProtectedContent = revision.ProtectedContent.ToArray(); revision.ProtectedContent[^1] ^= 1; }
        await f.Auth.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, [original.MessageId]));
        Assert.Null(error.InnerException); Assert.DoesNotContain(f.Payload().Text, error.Message);
    }

    [Fact]
    public async Task KeyProviderPrivateFailureIsSanitizedAndSourceContextCannotOutliveLaterEdit()
    {
        using var f = new Fixture(); var original = await f.CommitAsync(); var claim = await f.ClaimAsync();
        f.Keys.BeforeRead = () => throw new InvalidOperationException("owned-private-key-detail");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, [original.MessageId]));
        Assert.Null(error.InnerException); Assert.DoesNotContain("owned-private-key-detail", error.Message);
        f.Keys.BeforeRead = null;
        var context = await f.Reader.ReadAsync(claim, [original.MessageId]);
        await f.CommitAsync(f.Payload(eventId: "future-edit", kind: GroupSourceEventKind.Edit, text: "changed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.RequireCurrentAsync(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedSourceAndAccountCoverageGapsAreReportedAndChangesFenceContext(bool account)
    {
        using var f = new Fixture(); var original = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var context = await f.Reader.ReadAsync(claim, [original.MessageId]);
        if (account) f.Auth.Db.Add(new GroupAccountCoverageGapRecord
        {
            TenantId = f.Auth.Scope.TenantId,
            CompanyId = f.Auth.Scope.CompanyId,
            ConnectorAccountId = f.Auth.Account.Id,
            ListenerEpoch = 1,
            Reason = "owned-gap",
            OpenedAtUtc = f.Auth.Clock.Current,
            RecordedAtUtc = f.Auth.Clock.Current
        });
        else f.Auth.Db.Add(new GroupCoverageGapRecord
        {
            TenantId = f.Auth.Scope.TenantId,
            CompanyId = f.Auth.Scope.CompanyId,
            BindingId = f.Auth.Scope.SourceBindingId,
            Id = Guid.NewGuid(),
            AfterCommittedSequence = 1,
            Reason = "owned-gap",
            OpenedAtUtc = f.Auth.Clock.Current
        });
        await f.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.RequireCurrentAsync(context));
        Assert.True((await f.Reader.ReadAsync(claim, [original.MessageId])).HasCoverageGap);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        internal readonly GroupServiceGrantRecord Extract;
        internal readonly OwnedKeys Keys;
        internal GroupExtractionWorkerBinding Worker => new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, 1);
        internal GroupBatchSourceReader Reader => new(Auth.Db, Worker, Auth.Clock, Keys, new());
        internal Fixture()
        {
            Keys = new(Auth);
            Extract = new()
            {
                TenantId = Auth.Scope.TenantId,
                CompanyId = Auth.Scope.CompanyId,
                ServiceId = Auth.Service.Id,
                BindingId = Auth.Scope.SourceBindingId,
                Capability = GroupServiceCapability.Extract,
                IsEnabled = true
            };
            Auth.Account.QualificationJson = JsonSerializer.Serialize(new
            {
                Environment = GroupQualificationEnvironment.Synthetic,
                Observations = new[] { GroupConnectorCapability.EditEvents, GroupConnectorCapability.RecallEvents }.Select(x =>
                    new GroupConnectorObservation(x, GroupConnectorSupport.Supported, Guid.NewGuid(), GroupServiceAuthenticatorTests.Fixture.Now)).ToArray()
            },
                GroupServiceAuthenticator.JsonOptions);
            Auth.Db.AddRange(Extract, new GroupListenerLeaseRecord
            {
                TenantId = Auth.Scope.TenantId,
                CompanyId = Auth.Scope.CompanyId,
                ConnectorAccountId = Auth.Account.Id,
                OwnerId = Auth.Payload().ListenerOwnerId,
                Epoch = 1,
                HeartbeatAtUtc = Auth.Clock.Current,
                ExpiresAtUtc = Auth.Clock.Current.AddMinutes(20)
            });
            Auth.Db.SaveChanges();
        }
        internal GroupIngressPayload Payload(string messageId = "message", string eventId = "event", string text = "original private",
            GroupSourceEventKind kind = GroupSourceEventKind.NewText) => Auth.Payload() with
            {
                Text = text,
                Event = Auth.Payload().Event with
                {
                    MessageId = messageId,
                    RevisionEventId = eventId,
                    Kind = kind,
                    ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
                }
            };
        internal async Task<GroupIngressCommittedReceipt> CommitAsync(GroupIngressPayload? payload = null)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(payload ?? Payload(), GroupServiceAuthenticator.JsonOptions);
            var signature = Auth.Sign(body: body) with { SignedAtUnixSeconds = Auth.Clock.Current.ToUnixTimeSeconds(), Nonce = Guid.NewGuid() };
            signature = signature with
            {
                SignatureHex = Convert.ToHexString(HMACSHA256.HashData(Auth.Secrets.Key,
                GroupServiceAuthenticator.SigningBytes(signature, body)))
            };
            return await new GroupIngressStore(Auth.Db, Keys, new(), GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), Auth.Clock)
                .AcceptAsync(await Auth.Authenticator.AuthenticateAsync(signature, body));
        }
        internal async Task<GroupBatchAllocationReceipt> AllocateAsync()
        {
            Auth.Clock.Current += TimeSpan.FromSeconds(31);
            return (await new GroupBatchAllocationStore(Auth.Db, Worker, GroupBatchTiming.InitialTuning, Auth.Clock)
                .AllocateDueAsync(Auth.Scope, Guid.NewGuid()))!;
        }
        internal async Task<GroupBatchClaimHandle> ClaimAsync() => await ClaimAllocatedAsync((await AllocateAsync()).BatchId);
        internal async Task<GroupBatchClaimHandle> ClaimAllocatedAsync(Guid batch) => (await new GroupBatchClaimStore(Auth.Db, Worker, Auth.Clock)
            .TryAcquireAsync(Auth.Scope, batch, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5)))!.CurrentHandle!;
        internal async Task<string> CountsAsync() => string.Join("/", await Auth.Db.GroupMessageRevisions.CountAsync(),
            await Auth.Db.GroupIngressReceipts.CountAsync(), await Auth.Db.GroupIngressOutbox.CountAsync(), await Auth.Db.GroupBatchClaimReceipts.CountAsync(),
            await Auth.Db.GroupReaderGrants.CountAsync(), await Auth.Db.Users.CountAsync(), await Auth.Db.Tasks.CountAsync());
        public void Dispose() => Auth.Dispose();
    }

    private sealed class OwnedKeys(GroupServiceAuthenticatorTests.Fixture auth) : IGroupSourceKeyProvider
    {
        private readonly byte[] key = Enumerable.Repeat((byte)0x32, 32).ToArray();
        internal int Reads;
        internal Func<Task>? BeforeRead;
        internal GroupSourceKeyMaterial? LastMaterial;
        internal readonly TaskCompletionSource MaterialReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope scope, CancellationToken cancellationToken = default)
        { Assert.Equal(auth.Scope, scope); return ValueTask.FromResult(new GroupSourceKeyMaterial("owned", key)); }
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope scope, string keyId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(auth.Scope, scope); Assert.Equal("owned", keyId); Assert.Null(auth.Db.Database.CurrentTransaction);
            Reads++; if (BeforeRead is not null) await BeforeRead();
            LastMaterial = new("owned", key); MaterialReady.TrySetResult(); return LastMaterial;
        }
    }
}
