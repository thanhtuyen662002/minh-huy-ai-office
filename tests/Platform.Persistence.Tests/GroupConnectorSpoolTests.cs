using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupConnectorSpoolTests
{
    [Fact]
    public void ProtectedSpoolPreservesExactSourceAndZeroesReleasedCleartext()
    {
        using var fixture = new Fixture();
        foreach (var text in new[] { "", " \uFEFF Đúng nhóm 😀\n  ", new string('x', 8000) })
        {
            var admitted = fixture.Admit(fixture.Payload(text));
            var first = fixture.Protector.Protect(admitted, fixture.Key, "spool-v1");
            var second = fixture.Protector.Protect(admitted, fixture.Key, "spool-v1");
            Assert.NotEqual(first.Envelope, second.Envelope);
            Assert.Equal(admitted.Source, first.Context.Source);
            Assert.Equal(GroupIngressIdentity.EventIndex(admitted.Source, admitted.Payload.Event.RevisionEventId), first.Context.EventIdentityHash);
            Assert.InRange(first.Envelope.Length, 30, GroupSpoolContentProtector.MaximumEnvelopeBytes);
            using var clear = fixture.Protector.Unprotect(first.Context, first.Envelope, fixture.Key);
            Assert.Equal(admitted.Payload, GroupServiceAuthenticator.Parse(clear.Body));
            var alias = clear.Body; clear.Dispose();
            Assert.All(alias.ToArray(), value => Assert.Equal((byte)0, value));
            Assert.Throws<ObjectDisposedException>(() => clear.Body);
        }
    }

    [Theory]
    [InlineData("account")]
    [InlineData("empty-owner")]
    [InlineData("epoch")]
    [InlineData("expired")]
    [InlineData("long")]
    [InlineData("future-heartbeat")]
    [InlineData("qualification")]
    [InlineData("artifact")]
    [InlineData("live-synthetic")]
    public void RecoveryRefusesCurrentLeaseAndQualificationBeforePrivateDecryption(string change)
    {
        using var fixture = new Fixture(); var stored = fixture.Protector.Protect(fixture.Admit(fixture.Payload()), fixture.Key, "spool-v1");
        var lease = fixture.Lease; var current = fixture.Enrollment; var policy = Fixture.Policy;
        if (change == "account") lease = lease with { Account = lease.Account with { ConnectorAccountId = Guid.NewGuid() } };
        if (change == "empty-owner") lease = lease with { OwnerId = Guid.Empty };
        if (change == "epoch") lease = lease with { Epoch = 0 };
        if (change == "expired") lease = lease with { ExpiresAtUtc = Fixture.Now };
        if (change == "long") lease = lease with { ExpiresAtUtc = Fixture.Now.AddSeconds(31) };
        if (change == "future-heartbeat") lease = lease with { HeartbeatAtUtc = Fixture.Now.AddTicks(1) };
        if (change == "qualification") current = current with { Qualification = fixture.Qualification(account: Guid.NewGuid()) };
        if (change == "artifact") current = current with { Artifact = current.Artifact with { PackageVersion = "changed" } };
        if (change == "live-synthetic") policy = GroupIngressRuntimePolicy.Live;
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Protector.Recover(stored, new byte[16], current, lease, Fixture.Now, policy));
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("self")]
    [InlineData("echo")]
    public void NonSourcesAreFilteredBeforeEvenInvalidPrivateContentIsExamined(string change)
    {
        using var fixture = new Fixture();
        var payload = fixture.Payload() with { Event = null!, Text = null!, IsGroup = change != "dm", IsSelf = change == "self", IsKnownReportEcho = change == "echo" };
        Assert.Equal("Connector spool admission is unavailable.", Assert.Throws<UnauthorizedAccessException>(() => fixture.Admit(payload)).Message);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("account")]
    [InlineData("grant-disabled")]
    [InlineData("grant-capability")]
    [InlineData("service-disabled")]
    [InlineData("source-disabled")]
    [InlineData("lease-account")]
    [InlineData("lease-owner")]
    [InlineData("lease-epoch")]
    [InlineData("lease-expired")]
    [InlineData("lease-long")]
    [InlineData("lease-future")]
    [InlineData("qualification-account")]
    [InlineData("artifact")]
    public void UnenrolledOrObsoleteAuthorityCannotProduceSpoolAdmission(string change)
    {
        using var fixture = new Fixture();
        var payload = fixture.Payload(); var enrollment = fixture.Enrollment; var lease = fixture.Lease;
        if (change == "group") payload = payload with { Event = payload.Event with { Identity = payload.Event.Identity with { GroupId = payload.Event.Identity.GroupId + " " } } };
        if (change == "account") payload = payload with { Event = payload.Event with { Identity = payload.Event.Identity with { AccountId = payload.Event.Identity.AccountId.ToUpperInvariant() } } };
        if (change == "grant-disabled") enrollment = enrollment with { Grant = enrollment.Grant with { IsEnabled = false } };
        if (change == "grant-capability") enrollment = enrollment with { Grant = enrollment.Grant with { Capability = GroupServiceCapability.Notify } };
        if (change == "service-disabled") enrollment = enrollment with { Principal = enrollment.Principal with { IsEnabled = false } };
        if (change == "source-disabled") enrollment = enrollment with { Source = enrollment.Source with { IsEnabled = false } };
        if (change == "lease-account") lease = lease with { Account = lease.Account with { ConnectorAccountId = Guid.NewGuid() } };
        if (change == "lease-owner") lease = lease with { OwnerId = Guid.NewGuid() };
        if (change == "lease-epoch") lease = lease with { Epoch = 2 };
        if (change == "lease-expired") lease = lease with { ExpiresAtUtc = Fixture.Now };
        if (change == "lease-long") lease = lease with { ExpiresAtUtc = Fixture.Now.AddSeconds(31) };
        if (change == "lease-future") lease = lease with { HeartbeatAtUtc = Fixture.Now.AddTicks(1) };
        if (change == "qualification-account") enrollment = enrollment with { Qualification = fixture.Qualification(account: Guid.NewGuid()) };
        if (change == "artifact") enrollment = enrollment with { Artifact = enrollment.Artifact with { PackageVersion = "changed" } };
        Assert.ThrowsAny<Exception>(() => GroupConnectorSpoolAdmission.Filter(enrollment, payload, lease, Fixture.Now, Fixture.Policy));
    }

    [Fact]
    public void SyntheticQualificationNeverEnablesLiveSpoolAndOptionalEventsNeedFreshEvidence()
    {
        using var fixture = new Fixture();
        Assert.Throws<UnauthorizedAccessException>(() => GroupConnectorSpoolAdmission.Filter(fixture.Enrollment, fixture.Payload(), fixture.Lease, Fixture.Now, GroupIngressRuntimePolicy.Live));
        using var live = new Fixture(controlled: true);
        var controlled = live.Enrollment with { Qualification = live.Qualification(environment: GroupQualificationEnvironment.ControlledAccount) };
        Assert.NotNull(GroupConnectorSpoolAdmission.Filter(controlled, live.Payload(), live.Lease, Fixture.Now, GroupIngressRuntimePolicy.Live));
        Assert.Throws<UnauthorizedAccessException>(() => GroupConnectorSpoolAdmission.Filter(controlled, live.Payload(), live.Lease, Fixture.Now, Fixture.Policy));
        foreach (var kind in new[] { GroupSourceEventKind.Edit, GroupSourceEventKind.Recall })
        {
            var payload = fixture.Payload(kind == GroupSourceEventKind.Recall ? "" : "edit") with
            {
                Event = fixture.Payload().Event with
                {
                    Kind = kind,
                    ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind == GroupSourceEventKind.Recall ? "" : "edit")))
                }
            };
            var incomplete = fixture.Enrollment with { Qualification = fixture.Qualification(optional: false) };
            Assert.Throws<UnauthorizedAccessException>(() => GroupConnectorSpoolAdmission.Filter(incomplete, payload, fixture.Lease, Fixture.Now, Fixture.Policy));
            Assert.NotNull(fixture.Admit(payload));
        }
    }

    [Fact]
    public void MalformedChangedOverflowAndNonemptyRecallContentAreRefused()
    {
        using var fixture = new Fixture(); var payload = fixture.Payload();
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Admit(payload with { Text = "changed" }));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Admit(fixture.Payload(new string('x', 8001))));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Admit(payload with { Text = new string((char)0xd800, 1) }));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Admit(payload with { Event = payload.Event with { Kind = GroupSourceEventKind.Recall } }));
    }

    [Fact]
    public void EveryScopeEnrollmentDeletionKeyDigestAndCaptureChangeInvalidatesEncryptedSpool()
    {
        using var fixture = new Fixture(); var item = fixture.Protector.Protect(fixture.Admit(fixture.Payload()), fixture.Key, "spool-v1");
        var original = item.Context;
        var changed = new[]
        {
            original with { Source = original.Source with { TenantId = Guid.NewGuid() } },
            original with { Source = original.Source with { CompanyId = Guid.NewGuid() } },
            original with { Source = original.Source with { SourceBindingId = Guid.NewGuid() } },
            original with { ConnectorAccountId = Guid.NewGuid() }, original with { ServiceId = Guid.NewGuid() },
            original with { CredentialEpoch = 2 }, original with { SourceVersion = 2 }, original with { GrantVersion = 2 },
            original with { DeletionGeneration = 1 }, original with { ExternalIdentityHash = new string('A', 64) },
            original with { EventIdentityHash = new string('B', 64) }, original with { BodySha256 = new string('C', 64) },
            original with { AdmittedAtUtc = original.AdmittedAtUtc.AddTicks(1) }, original with { KeyId = "other-key" }
        };
        foreach (var context in changed)
            Assert.Equal("Connector spool content is unavailable.", Assert.Throws<InvalidOperationException>(() => fixture.Protector.Unprotect(context, item.Envelope, fixture.Key)).Message);
        Assert.Throws<InvalidOperationException>(() => fixture.Protector.Unprotect(original, item.Envelope, new byte[32]));
        using var valid = fixture.Protector.Unprotect(original, item.Envelope, fixture.Key);
        Assert.Equal(fixture.Payload().Text, GroupServiceAuthenticator.Parse(valid.Body).Text);
    }

    [Fact]
    public void TamperTruncationAdjacentEnvelopeOverflowAndWrongKeyShapesAreBounded()
    {
        using var fixture = new Fixture(); var admitted = fixture.Admit(fixture.Payload());
        var item = fixture.Protector.Protect(admitted, fixture.Key, "spool-v1");
        foreach (var offset in new[] { 0, 1, 12, 13, 28, 29, item.Envelope.Length - 1 })
        {
            var tampered = item.Envelope.ToArray(); tampered[offset] ^= 1;
            Assert.Equal("Connector spool content is unavailable.", Assert.Throws<InvalidOperationException>(() => fixture.Protector.Unprotect(item.Context, tampered, fixture.Key)).Message);
        }
        Assert.Throws<InvalidOperationException>(() => fixture.Protector.Unprotect(item.Context, item.Envelope.AsSpan(0, 29), fixture.Key));
        Assert.Throws<InvalidOperationException>(() => fixture.Protector.Unprotect(item.Context, new byte[GroupSpoolContentProtector.MaximumEnvelopeBytes + 1], fixture.Key));
        Assert.Throws<InvalidOperationException>(() => fixture.Protector.Protect(admitted, new byte[16], "spool-v1"));
        Assert.Throws<InvalidOperationException>(() => fixture.Protector.Protect(admitted, fixture.Key, "key/path"));
    }

    private sealed class Fixture : IDisposable
    {
        internal static DateTimeOffset Now => GroupServiceAuthenticatorTests.Fixture.Now;
        internal static GroupIngressRuntimePolicy Policy => GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true);
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth;
        internal Fixture(bool controlled = false) { Auth = new(controlled: controlled); }
        internal readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        internal readonly GroupSpoolContentProtector Protector = new();
        internal GroupConnectorArtifact Artifact => new(Auth.External.Provider, Auth.Account.PackageVersion, Auth.Account.GitCommit);
        internal GroupConnectorQualification Qualification(Guid? account = null, GroupQualificationEnvironment environment = GroupQualificationEnvironment.Synthetic, bool optional = true) =>
            new(Auth.Scope.TenantId, Auth.Scope.CompanyId, account ?? Auth.Account.Id, Auth.External.AccountId, Artifact, environment,
                Enum.GetValues<GroupConnectorCapability>().Where(x => optional || x is not GroupConnectorCapability.EditEvents and not GroupConnectorCapability.RecallEvents)
                    .Select(x => new GroupConnectorObservation(x, GroupConnectorSupport.Supported, Guid.NewGuid(), Now)).ToArray());
        internal GroupConnectorEnrollment Enrollment => new(new(Auth.Service.Id, Auth.Service.CredentialEpoch),
            new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, Auth.Service.CredentialEpoch, true),
            new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, Auth.Scope.SourceBindingId, GroupServiceCapability.Ingest, 1, true),
            new(Auth.Scope, Auth.Account.Id, Auth.External, "Owned spool source", 1, 0, true), Qualification(), Artifact);
        internal GroupListenerLeaseSnapshot Lease => new(new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id), Auth.Payload().ListenerOwnerId, 1, Now, Now.AddSeconds(30));
        internal GroupIngressPayload Payload(string text = "owned private spool text")
        {
            var payload = Auth.Payload();
            return payload with { Text = text, Event = payload.Event with { ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) } };
        }
        internal GroupConnectorSpoolAdmission Admit(GroupIngressPayload payload) => GroupConnectorSpoolAdmission.Filter(Enrollment, payload, Lease, Now, Policy);
        public void Dispose() { CryptographicOperations.ZeroMemory(Key); Auth.Dispose(); }
    }

    [Theory]
    [InlineData("synthetic")]
    [InlineData("owned-fixture")]
    public void KnownFixtureArtifactsCannotBePromotedByControlledQualificationMetadata(string marker)
    {
        using var fixture = new Fixture(controlled: true);
        var artifact = fixture.Artifact with
        {
            Provider = marker == "synthetic" ? "synthetic" : fixture.Artifact.Provider,
            PackageVersion = marker == "owned-fixture" ? "owned-fixture" : fixture.Artifact.PackageVersion
        };
        var source = fixture.Enrollment.Source with { ExternalIdentity = fixture.Enrollment.Source.ExternalIdentity with { Provider = artifact.Provider } };
        var qualification = new GroupConnectorQualification(source.Scope.TenantId, source.Scope.CompanyId, source.ConnectorAccountId,
            source.ExternalIdentity.AccountId, artifact, GroupQualificationEnvironment.ControlledAccount,
            fixture.Qualification().Observations);
        var current = fixture.Enrollment with { Artifact = artifact, Source = source, Qualification = qualification };
        var payload = fixture.Payload() with { Event = fixture.Payload().Event with { Identity = source.ExternalIdentity } };
        Assert.Throws<UnauthorizedAccessException>(() => GroupConnectorSpoolAdmission.Filter(current, payload, fixture.Lease, Fixture.Now, GroupIngressRuntimePolicy.Live));
        // A fixture spool captured under the isolated Development policy cannot
        // become live merely by changing its trusted qualification metadata.
        using var owned = new Fixture();
        var stored = owned.Protector.Protect(owned.Admit(owned.Payload()), owned.Key, "spool-v1");
        var promoted = owned.Enrollment with { Qualification = owned.Qualification(environment: GroupQualificationEnvironment.ControlledAccount) };
        Assert.Throws<UnauthorizedAccessException>(() => owned.Protector.Recover(stored, new byte[16], promoted, owned.Lease, Fixture.Now, GroupIngressRuntimePolicy.Live));
    }

    [Fact]
    public void RestartRecoveryRechecksCurrentEnrollmentAndRewrapsOnlyTransportOwnership()
    {
        using var fixture = new Fixture(); var original = fixture.Admit(fixture.Payload());
        var stored = fixture.Protector.Protect(original, fixture.Key, "spool-v1");
        var now = Fixture.Now.AddSeconds(5);
        var lease = fixture.Lease with { OwnerId = Guid.NewGuid(), Epoch = 2, HeartbeatAtUtc = now, ExpiresAtUtc = now.AddSeconds(30) };
        var recovered = fixture.Protector.Recover(stored, fixture.Key, fixture.Enrollment, lease, now, Fixture.Policy);
        Assert.Equal(original.Payload.Event, recovered.Payload.Event); Assert.Equal(original.Payload.Text, recovered.Payload.Text);
        Assert.Equal(lease.OwnerId, recovered.Payload.ListenerOwnerId); Assert.Equal(2, recovered.Payload.ListenerEpoch);
        Assert.Equal(GroupIngressStore.EnvelopeHash(original.Payload.Event, original.Payload.Text), GroupIngressStore.EnvelopeHash(recovered.Payload.Event, recovered.Payload.Text));
        Assert.Equal(original.Source, recovered.Source);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("source")]
    [InlineData("account")]
    [InlineData("service")]
    [InlineData("credential")]
    [InlineData("version")]
    [InlineData("grant-version")]
    [InlineData("deletion")]
    [InlineData("disabled")]
    public void RecoveryRefusesObsoleteScopeOrDeletionBeforePrivateDecryption(string change)
    {
        using var fixture = new Fixture(); var stored = fixture.Protector.Protect(fixture.Admit(fixture.Payload()), fixture.Key, "spool-v1");
        var current = fixture.Enrollment; var source = current.Source;
        if (change == "tenant") source = source with { Scope = source.Scope with { TenantId = Guid.NewGuid() } };
        if (change == "company") source = source with { Scope = source.Scope with { CompanyId = Guid.NewGuid() } };
        if (change == "source") source = source with { Scope = source.Scope with { SourceBindingId = Guid.NewGuid() } };
        if (change == "account") source = source with { ConnectorAccountId = Guid.NewGuid() };
        if (change == "version") source = source with { Version = 2 };
        if (change == "deletion") source = source with { DeletionGeneration = 1 };
        if (change == "disabled") source = source with { IsEnabled = false };
        current = current with { Source = source };
        if (change == "service") current = current with { Principal = current.Principal with { ServiceId = Guid.NewGuid() } };
        if (change == "credential") current = current with { Principal = current.Principal with { CredentialEpoch = 2 } };
        if (change == "grant-version") current = current with { Grant = current.Grant with { Version = 2 } };
        // Invalid key would cause a content error if private decryption ran.
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Protector.Recover(stored, new byte[16], current, fixture.Lease, Fixture.Now, Fixture.Policy));
    }
}
