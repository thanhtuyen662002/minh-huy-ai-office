using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupConnectorFileSpoolTests
{
    [Fact]
    public void CiphertextAndExactUnicodeSurviveCloseReopenWithoutKeysInFiles()
    {
        using var fixture = new Fixture(); var item = fixture.Item("private spool 😀 \uFEFF\n  ");
        GroupSpoolItemReference reference;
        using (var spool = fixture.Open())
        {
            reference = spool.Append(item);
            Assert.Equal(item.Context, Assert.Single(spool.Pending()).Context);
            Assert.Equal(item.Envelope, spool.Load(reference).Envelope);
        }
        var bytes = File.ReadAllBytes(fixture.Path(item, ".spool"));
        Assert.DoesNotContain("private spool", Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.AsSpan().IndexOf(fixture.Key) >= 0);
        using var reopened = fixture.Open();
        var retained = reopened.Load(Assert.Single(reopened.Pending()));
        using var clear = fixture.Protector.Unprotect(retained.Context, retained.Envelope, fixture.Key);
        Assert.Equal("private spool 😀 \uFEFF\n  ", GroupServiceAuthenticator.Parse(clear.Body).Text);
        Assert.Equal(reference.Context, retained.Context);
    }

    [Fact]
    public async Task ConcurrentHundredExactCapturesRetainOneOriginalCiphertext()
    {
        using var fixture = new Fixture(); var item = fixture.Item(); using var spool = fixture.Open();
        var admitted = spool.Append(item); var original = File.ReadAllBytes(fixture.Path(item, ".spool"));
        var receipts = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => spool.Append(item))));
        Assert.All(receipts, x => Assert.Equal(admitted.Context, x.Context));
        Assert.Single(spool.Pending()); Assert.Equal(original, File.ReadAllBytes(fixture.Path(item, ".spool")));
        var changedCaptureTime = item with { Context = item.Context with { AdmittedAtUtc = item.Context.AdmittedAtUtc.AddSeconds(1) } };
        Assert.Equal(admitted.Context, spool.Append(changedCaptureTime).Context);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(item, ".spool")));
    }

    [Fact]
    public void ChangedSameEventNeverOverwritesBacklog()
    {
        using var fixture = new Fixture(); var item = fixture.Item(); using var spool = fixture.Open();
        spool.Append(item); var original = File.ReadAllBytes(fixture.Path(item, ".spool"));
        var changes = new[]
        {
            item.Context with { BodySha256 = new string('A', 64) }, item.Context with { SourceVersion = 2 },
            item.Context with { GrantVersion = 2 }, item.Context with { DeletionGeneration = 1 },
            item.Context with { CredentialEpoch = 2 }, item.Context with { EventKind = GroupSourceEventKind.Edit },
            item.Context with { KeyId = "rotated" }
        };
        foreach (var context in changes) Assert.Throws<IOException>(() => spool.Append(item with { Context = context }));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(item, ".spool"))); Assert.Single(spool.Pending());
    }

    [Fact]
    public void ExclusiveOwnerAndScopedDirectoriesPersistAcrossReopen()
    {
        using var fixture = new Fixture(); var item = fixture.Item();
        using (var first = fixture.Open())
        {
            first.Append(item);
            Assert.Throws<IOException>(() => fixture.Open());
            using var other = GroupConnectorFileSpool.Open(fixture.Root, fixture.Account with { ConnectorAccountId = Guid.NewGuid() }, fixture.Auth.Service.Id);
            Assert.Empty(other.Pending());
        }
        using var reopened = fixture.Open(); Assert.Single(reopened.Pending());
    }

    [Fact]
    public void CountCapacityKeepsEveryByteAndAcceptsExactReconciliation()
    {
        using var fixture = new Fixture(); var item = fixture.Item(); using var spool = fixture.Open(new(MaximumItems: 1));
        spool.Append(item); var original = File.ReadAllBytes(fixture.Path(item, ".spool"));
        Assert.Throws<GroupSpoolCapacityException>(() => spool.Append(fixture.Item(eventId: "second-event")));
        Assert.Single(spool.Pending()); Assert.Equal(original, File.ReadAllBytes(fixture.Path(item, ".spool")));
        Assert.Equal(item.Context, spool.Append(item).Context);
        Assert.Empty(Directory.GetFiles(fixture.Folder, "*.pending"));
    }

    [Fact]
    public void ByteCapacityKeepsBacklogAndHasNoEvictionOrPartialNewEntry()
    {
        using var fixture = new Fixture(); var item = fixture.Item(new string('\uFFFF', 8000));
        using var spool = fixture.Open(new(MaximumBytes: GroupConnectorFileSpool.MaximumRecordBytes));
        spool.Append(item); var original = File.ReadAllBytes(fixture.Path(item, ".spool"));
        Assert.Throws<GroupSpoolCapacityException>(() => spool.Append(fixture.Item(new string('\uFFFF', 8000), "second-event")));
        Assert.Single(spool.Pending()); Assert.Equal(original, File.ReadAllBytes(fixture.Path(item, ".spool")));
        Assert.Empty(Directory.GetFiles(fixture.Folder, "*.pending"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteStagingRecoversAfterRestartWithoutLosingOriginalRecord(bool duplicate)
    {
        using var fixture = new Fixture(); var item = fixture.Item();
        using (var spool = fixture.Open()) spool.Append(item);
        var original = File.ReadAllBytes(fixture.Path(item, ".spool"));
        if (duplicate) File.Copy(fixture.Path(item, ".spool"), fixture.Path(item, ".pending"));
        else File.Move(fixture.Path(item, ".spool"), fixture.Path(item, ".pending"));
        using var recovered = fixture.Open(); Assert.Single(recovered.Pending());
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(item, ".spool")));
        Assert.False(File.Exists(fixture.Path(item, ".pending")));
    }

    [Theory]
    [InlineData("torn")]
    [InlineData("extra")]
    [InlineData("magic")]
    [InlineData("name")]
    [InlineData("foreign")]
    public void CorruptOrForeignBacklogRefusesAndPreservesEvidence(string change)
    {
        using var fixture = new Fixture(); var item = fixture.Item();
        using (var spool = fixture.Open()) spool.Append(item);
        var path = fixture.Path(item, ".spool"); var original = File.ReadAllBytes(path);
        if (change == "torn") { path = fixture.Path(item, ".pending"); File.Delete(fixture.Path(item, ".spool")); File.WriteAllBytes(path, original[..20]); }
        if (change == "extra") File.WriteAllText(System.IO.Path.Combine(fixture.Folder, "unexpected.txt"), "owned marker");
        if (change == "magic") { var bytes = original.ToArray(); bytes[0] ^= 1; File.WriteAllBytes(path, bytes); }
        if (change == "name") { var target = System.IO.Path.Combine(fixture.Folder, new string('A', 64) + ".pending"); File.Move(path, target); path = target; }
        if (change == "foreign") { var bytes = original.ToArray(); bytes[8] ^= 1; File.WriteAllBytes(path, bytes); }
        var evidence = File.ReadAllBytes(path);
        // Open validates staging; settled records are checked before exposing
        // references or private content, never silently discarded on corruption.
        Assert.Throws<IOException>(() => { using var recovered = fixture.Open(); recovered.Pending(); });
        Assert.Equal(evidence, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("account")]
    [InlineData("service")]
    public void ForeignScopeCannotEnterAnotherAccountSpool(string change)
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var item = fixture.Item(); var context = item.Context;
        if (change == "tenant") context = context with { Source = context.Source with { TenantId = Guid.NewGuid() } };
        if (change == "company") context = context with { Source = context.Source with { CompanyId = Guid.NewGuid() } };
        if (change == "account") context = context with { ConnectorAccountId = Guid.NewGuid() };
        if (change == "service") context = context with { ServiceId = Guid.NewGuid() };
        Assert.Throws<IOException>(() => spool.Append(item with { Context = context })); Assert.Empty(spool.Pending());
    }

    [Fact]
    public void OnlyMatchingTrustedCommitCanRemoveExactRetainedCapture()
    {
        using var fixture = new Fixture(); using var spool = fixture.Open(); var item = fixture.Item(); var reference = spool.Append(item);
        var committed = new GroupIngressCommittedReceipt(item.Context.Source, Guid.NewGuid(), 1, 1, GroupServiceAuthenticatorTests.Fixture.Now, false);
        Assert.Throws<IOException>(() => spool.Acknowledge(reference, committed with { Source = committed.Source with { CompanyId = Guid.NewGuid() } }));
        Assert.Throws<IOException>(() => spool.Acknowledge(reference, committed with { Revision = 0 }));
        Assert.Throws<IOException>(() => spool.Acknowledge(new(item.Context with { BodySha256 = new string('A', 64) }), committed));
        Assert.Single(spool.Pending());
        Assert.True(spool.Acknowledge(reference, committed)); Assert.False(spool.Acknowledge(reference, committed with { WasAlreadyCommitted = true }));
        Assert.Empty(spool.Pending());
    }

    [Fact]
    public void InvalidRootsLimitsEnvelopeAndDisposedOwnerAreBounded()
    {
        using var fixture = new Fixture();
        foreach (var root in new[] { "relative", System.IO.Path.GetPathRoot(fixture.Root)!, System.IO.Path.Combine(fixture.Root, "missing") })
            Assert.Throws<IOException>(() => GroupConnectorFileSpool.Open(root, fixture.Account, fixture.Auth.Service.Id));
        Assert.Throws<IOException>(() => fixture.Open(new(MaximumItems: 0)));
        Assert.Throws<IOException>(() => fixture.Open(new(MaximumBytes: 1)));
        using var spool = fixture.Open(); var item = fixture.Item();
        Assert.Throws<IOException>(() => spool.Append(item with { Envelope = new byte[GroupSpoolContentProtector.MaximumEnvelopeBytes + 1] }));
        Assert.Throws<IOException>(() => spool.Append(item with { Context = item.Context with { KeyId = "../private" } }));
        spool.Dispose(); Assert.Throws<ObjectDisposedException>(() => spool.Pending());
        using var reopened = fixture.Open(); Assert.Empty(reopened.Pending());
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aioffice-owned-spool-" + Guid.NewGuid().ToString("N"));
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        internal readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        internal readonly GroupSpoolContentProtector Protector = new();
        internal GroupListenerAccountScope Account => new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id);
        internal string Folder => System.IO.Path.Combine(Root, "group-spool", $"{Account.TenantId:N}_{Account.CompanyId:N}_{Account.ConnectorAccountId:N}_{Auth.Service.Id:N}");
        internal Fixture() { Directory.CreateDirectory(Root); }
        internal GroupConnectorFileSpool Open(GroupSpoolStorageLimits? limits = null) => GroupConnectorFileSpool.Open(Root, Account, Auth.Service.Id, limits);
        internal string Path(GroupSpoolProtectedContent item, string extension) => System.IO.Path.Combine(Folder, item.Context.EventIdentityHash + extension);
        internal GroupSpoolProtectedContent Item(string text = "owned spool body", string eventId = "owned-event")
        {
            var artifact = new GroupConnectorArtifact(Auth.External.Provider, Auth.Account.PackageVersion, Auth.Account.GitCommit);
            var source = new SourceGroupBinding(Auth.Scope, Auth.Account.Id, Auth.External, "Owned fixture source", 1, 0, true);
            var qualification = new GroupConnectorQualification(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id, Auth.External.AccountId,
                artifact, GroupQualificationEnvironment.Synthetic, []);
            var enrollment = new GroupConnectorEnrollment(new(Auth.Service.Id, 1), new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, 1, true),
                new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, Auth.Scope.SourceBindingId, GroupServiceCapability.Ingest, 1, true), source, qualification, artifact);
            var payload = Auth.Payload(); payload = payload with
            {
                Text = text,
                Event = payload.Event with
                {
                    RevisionEventId = eventId,
                    ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
                }
            };
            var now = GroupServiceAuthenticatorTests.Fixture.Now;
            var admission = GroupConnectorSpoolAdmission.Filter(enrollment, payload, new(Account, payload.ListenerOwnerId, 1, now, now.AddSeconds(30)),
                now, GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true));
            return Protector.Protect(admission, Key, "spool-v1");
        }
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key); Auth.Dispose();
            var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetFileName(Root)));
            if (System.IO.Path.GetFullPath(Root) != expected || !System.IO.Path.GetFileName(Root).StartsWith("aioffice-owned-spool-", StringComparison.Ordinal))
                throw new InvalidOperationException("Owned fixture cleanup boundary is invalid.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
