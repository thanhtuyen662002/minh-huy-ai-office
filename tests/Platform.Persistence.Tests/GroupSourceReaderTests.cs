using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupSourceReaderTests
{
    [Fact]
    public async Task ExplicitReaderReceivesExactProtectedOriginalWithoutPortalTaskOrWriteEffects()
    {
        using var fixture = new Fixture();
        var original = fixture.Auth.Payload() with { Event = fixture.Auth.Payload().Event with { MessageId = "message😀� \uFEFF", RevisionEventId = "event😀 \uFEFF", ReplyToMessageId = "reply😀 " } };
        var receipt = await fixture.CommitAsync(original);
        var before = await fixture.CountsAsync();
        var view = (await fixture.Reader.GetAsync(fixture.Authority, fixture.Auth.Scope.SourceBindingId, receipt.MessageId))!;
        Assert.Equal(original.Text, view.Text); Assert.Equal(original.Event.MessageId, view.ExternalMessageId);
        Assert.Equal(original.Event.ReplyToMessageId, view.ReplyToMessageId); Assert.Equal(receipt.CommittedSequence, view.CommittedSequence);
        Assert.Equal(fixture.Auth.Scope, view.Source); Assert.False(view.HasCoverageGap); Assert.Equal(1, fixture.Keys.Reads);
        Assert.Equal(before, await fixture.CountsAsync()); Assert.Empty(await fixture.Auth.Db.Tasks.ToArrayAsync()); Assert.Empty(await fixture.Auth.Db.Users.ToArrayAsync());
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("foreign-company")]
    [InlineData("foreign-user")]
    [InlineData("source")]
    [InlineData("role")]
    [InlineData("identity")]
    [InlineData("grant-version")]
    public async Task AdministratorMembershipCannotReplaceExplicitExactCurrentReaderAuthority(string change)
    {
        using var fixture = new Fixture(); var receipt = await fixture.CommitAsync();
        var authority = fixture.Authority;
        switch (change)
        {
            case "grant": fixture.Grant.IsEnabled = false; break;
            case "foreign-company": authority = AuthorizationContext.Create(authority.TenantId, Guid.NewGuid(), authority.UserId); break;
            case "foreign-user": authority = AuthorizationContext.Create(authority.TenantId, authority.CompanyId, Guid.NewGuid()); break;
            case "source": fixture.Auth.Binding.IsEnabled = false; break;
            case "role": fixture.Auth.Binding.Role = GroupBindingRole.TechnicalInternal; break;
            case "identity": fixture.Auth.Binding.PhysicalGroupHash = new string('0', 64); break;
            case "grant-version": fixture.Grant.Version = 0; break;
        }
        await fixture.Auth.Db.SaveChangesAsync(); var before = await fixture.CountsAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Reader.GetAsync(authority, fixture.Auth.Scope.SourceBindingId, receipt.MessageId));
        Assert.Equal(0, fixture.Keys.Reads); Assert.Equal(before, await fixture.CountsAsync());
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("grant-version")]
    [InlineData("source")]
    [InlineData("source-version")]
    [InlineData("deletion")]
    [InlineData("membership")]
    public async Task RevocationDuringPrivateKeyWorkDiscardsMaterializedOriginal(string change)
    {
        using var fixture = new Fixture(); var receipt = await fixture.CommitAsync();
        fixture.Keys.BeforeRead = async () =>
        {
            switch (change)
            {
                case "grant": fixture.Grant.IsEnabled = false; break;
                case "grant-version": fixture.Grant.Version++; break;
                case "source": fixture.Auth.Binding.IsEnabled = false; break;
                case "source-version": fixture.Auth.Binding.Version++; break;
                case "deletion": fixture.Auth.Binding.DeletionGeneration++; break;
                case "membership": fixture.Directory.Enabled = false; break;
            }
            await fixture.Auth.Db.SaveChangesAsync();
        };
        var before = await fixture.CountsAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Reader.GetAsync(fixture.Authority, fixture.Auth.Scope.SourceBindingId, receipt.MessageId));
        Assert.Equal(1, fixture.Keys.Reads); Assert.Equal(before, await fixture.CountsAsync());
    }

    [Theory]
    [InlineData("cipher")]
    [InlineData("receipt")]
    [InlineData("message")]
    [InlineData("sender")]
    public async Task CorruptProtectedOriginalOrEnvelopeCannotBecomeReadableContent(string corruption)
    {
        using var fixture = new Fixture(); var receipt = await fixture.CommitAsync();
        var row = await fixture.Auth.Db.GroupMessageRevisions.SingleAsync();
        if (corruption == "cipher") { row.ProtectedContent = row.ProtectedContent.ToArray(); row.ProtectedContent[^1] ^= 1; }
        if (corruption == "receipt") (await fixture.Auth.Db.GroupIngressReceipts.SingleAsync()).EnvelopeSha256 = new string('0', 64);
        if (corruption == "message") (await fixture.Auth.Db.GroupMessages.SingleAsync()).IdentityHash = new string('0', 64);
        if (corruption == "sender") row.SenderId = "changed-sender";
        await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Reader.GetAsync(fixture.Authority, fixture.Auth.Scope.SourceBindingId, receipt.MessageId));
    }

    [Fact]
    public async Task RecallAndEditPrecedenceCannotBeUndoneByLateOriginalOrLaterEdit()
    {
        using var fixture = new Fixture();
        var edit = fixture.Payload(GroupSourceEventKind.Edit, "edit-event", "Changed original");
        var receipt = await fixture.CommitAsync(edit);
        Assert.Equal(edit.Text, (await fixture.ReadAsync(receipt.MessageId))!.Text);
        await fixture.CommitAsync(fixture.Payload(GroupSourceEventKind.Recall, "recall-event", ""));
        await fixture.CommitAsync(fixture.Auth.Payload());
        await fixture.CommitAsync(fixture.Payload(GroupSourceEventKind.Edit, "later-edit", "Must not restore recalled text"));
        var view = (await fixture.ReadAsync(receipt.MessageId))!;
        Assert.Equal(GroupSourceEventKind.Recall, view.Kind); Assert.Null(view.Text); Assert.Equal(2, view.Revision); Assert.True(view.HasCoverageGap);
        Assert.Single(await fixture.Auth.Db.GroupCoverageGaps.ToArrayAsync());
    }

    [Fact]
    public async Task RecallCommittedDuringKeyAwaitRejectsEarlierPrivateBodyAndRestoresRecalledProjection()
    {
        using var fixture = new Fixture(); var receipt = await fixture.CommitAsync();
        fixture.Keys.BeforeRead = async () => await fixture.CommitAsync(fixture.Payload(GroupSourceEventKind.Recall, "late-recall", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(receipt.MessageId));
        fixture.Keys.BeforeRead = null;
        var recalled = (await fixture.ReadAsync(receipt.MessageId))!;
        Assert.Equal(GroupSourceEventKind.Recall, recalled.Kind); Assert.Null(recalled.Text);
    }

    [Fact]
    public async Task RecallCommittedDuringFinalAuthorityAwaitRejectsEarlierBodyAndFreshReadShowsRecall()
    {
        using var fixture = new Fixture(); var receipt = await fixture.CommitAsync();
        fixture.Directory.BeforeResolve = async call =>
        {
            if (call == 3) await fixture.CommitAsync(fixture.Payload(GroupSourceEventKind.Recall, "final-authority-recall", ""));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(receipt.MessageId));
        Assert.Equal(3, fixture.Directory.Resolves);
        fixture.Directory.BeforeResolve = null;
        var recalled = (await fixture.ReadAsync(receipt.MessageId))!;
        Assert.Equal(GroupSourceEventKind.Recall, recalled.Kind); Assert.Null(recalled.Text);
        Assert.Equal(2, await fixture.Auth.Db.GroupMessageRevisions.CountAsync());
    }

    [Theory]
    [InlineData("same-account")]
    [InlineData("other-tenant")]
    [InlineData("other-company")]
    [InlineData("other-account")]
    public async Task AccountInterruptionAppearsOnPrivateBodyAndHeadCatalogOnlyInCurrentAccountScope(string scope)
    {
        using var fixture = new Fixture(); var receipt = await fixture.CommitAsync();
        Assert.False((await fixture.ReadAsync(receipt.MessageId))!.HasCoverageGap);
        fixture.Auth.Db.Add(new GroupAccountCoverageGapRecord
        {
            TenantId = scope == "other-tenant" ? Guid.NewGuid() : fixture.Auth.Scope.TenantId,
            CompanyId = scope == "other-company" ? Guid.NewGuid() : fixture.Auth.Scope.CompanyId,
            ConnectorAccountId = scope == "other-account" ? Guid.NewGuid() : fixture.Auth.Account.Id,
            ListenerEpoch = 1,
            Reason = "listener-started",
            OpenedAtUtc = GroupServiceAuthenticatorTests.Fixture.Now,
            RecordedAtUtc = GroupServiceAuthenticatorTests.Fixture.Now
        });
        await fixture.Auth.Db.SaveChangesAsync();
        Assert.Equal(scope == "same-account", (await fixture.ReadAsync(receipt.MessageId))!.HasCoverageGap);
        var heads = await fixture.Reader.ListMessagesAsync(fixture.Authority, fixture.Auth.Scope.SourceBindingId);
        Assert.Equal(scope == "same-account", heads.HasCoverageGap); Assert.Single(heads.Items);
        Assert.Empty(fixture.Auth.Db.GroupCoverageGaps);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        internal readonly OwnedKeys Keys = new();
        internal readonly OwnedDirectory Directory = new();
        internal readonly GroupReaderGrantRecord Grant;
        internal readonly AuthorizationContext Authority;
        internal GroupSourceReader Reader => new(Auth.Db, Directory, Keys, new());
        internal Fixture()
        {
            Authority = AuthorizationContext.Create(Auth.Scope.TenantId, Auth.Scope.CompanyId, Guid.NewGuid());
            Grant = new() { TenantId = Authority.TenantId, CompanyId = Authority.CompanyId, UserId = Authority.UserId, BindingId = Auth.Scope.SourceBindingId, IsEnabled = true };
            Auth.Account.QualificationJson = JsonSerializer.Serialize(new
            {
                Environment = GroupQualificationEnvironment.Synthetic,
                Observations = new[] { GroupConnectorCapability.EditEvents, GroupConnectorCapability.RecallEvents }.Select(capability =>
                    new GroupConnectorObservation(capability, GroupConnectorSupport.Supported, Guid.NewGuid(), GroupServiceAuthenticatorTests.Fixture.Now)).ToArray()
            }, GroupServiceAuthenticator.JsonOptions);
            Auth.Db.AddRange(Grant, new GroupListenerLeaseRecord
            {
                TenantId = Auth.Scope.TenantId,
                CompanyId = Auth.Scope.CompanyId,
                ConnectorAccountId = Auth.Account.Id,
                OwnerId = Auth.Payload().ListenerOwnerId,
                Epoch = 1,
                HeartbeatAtUtc = GroupServiceAuthenticatorTests.Fixture.Now,
                ExpiresAtUtc = GroupServiceAuthenticatorTests.Fixture.Now.AddMinutes(2)
            });
            Auth.Db.SaveChanges();
        }
        internal GroupIngressPayload Payload(GroupSourceEventKind kind, string revision, string text) => Auth.Payload() with
        {
            Text = text,
            Event = Auth.Payload().Event with { Kind = kind, RevisionEventId = revision, ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) }
        };
        internal async Task<GroupIngressCommittedReceipt> CommitAsync(GroupIngressPayload? payload = null)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(payload ?? Auth.Payload(), GroupServiceAuthenticator.JsonOptions);
            var verified = await Auth.Authenticator.AuthenticateAsync(Auth.Sign(body: body), body);
            return await new GroupIngressStore(Auth.Db, Keys, new(), GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), Auth.Clock).AcceptAsync(verified);
        }
        internal Task<GroupSourceMessageView?> ReadAsync(Guid message) => Reader.GetAsync(Authority, Auth.Scope.SourceBindingId, message);
        internal async Task<int[]> CountsAsync() => [await Auth.Db.GroupMessages.CountAsync(), await Auth.Db.GroupMessageRevisions.CountAsync(),
            await Auth.Db.GroupIngressReceipts.CountAsync(), await Auth.Db.GroupIngressOutbox.CountAsync()];
        public void Dispose() => Auth.Dispose();
    }
    private sealed class OwnedDirectory : IAuthorizationDirectory
    {
        internal bool Enabled = true;
        internal int Resolves;
        internal Func<int, Task>? BeforeResolve;
        public async Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
        {
            Resolves++;
            if (BeforeResolve is not null) await BeforeResolve(Resolves);
            return Enabled ? new AuthorizationDirectoryEntry(context, ["admin"]) : null;
        }
    }
    private sealed class OwnedKeys : IGroupSourceKeyProvider
    {
        private readonly byte[] key = Enumerable.Repeat((byte)0x32, 32).ToArray();
        internal int Reads;
        internal Func<Task>? BeforeRead;
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default) => ValueTask.FromResult(new GroupSourceKeyMaterial("owned", key));
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default)
        { Reads++; Assert.Equal("owned", keyId); if (BeforeRead is not null) await BeforeRead(); return new("owned", key); }
    }
}
