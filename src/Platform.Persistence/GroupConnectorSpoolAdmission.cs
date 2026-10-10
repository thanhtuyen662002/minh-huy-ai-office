using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupConnectorEnrollment(GroupServiceAuthentication Authentication,
    GroupServicePrincipal Principal, GroupServiceGrant Grant, SourceGroupBinding Source,
    GroupConnectorQualification Qualification, GroupConnectorArtifact Artifact);

// This is a mechanical prefilter over trusted enrollment/lease snapshots, not a
// service authenticator. The connector host must obtain these from the backend;
// no customer/HTTP/model-supplied enrollment is accepted as authority. Recovery
// must obtain fresh snapshots and recheck source version/deletion before send.
public sealed class GroupConnectorSpoolAdmission
{
    private GroupConnectorSpoolAdmission(GroupConnectorEnrollment enrollment, GroupIngressPayload payload, GroupListenerLeaseSnapshot lease, DateTimeOffset admittedAtUtc)
    { Enrollment = enrollment; Payload = payload; Lease = lease; AdmittedAtUtc = admittedAtUtc; }
    internal GroupConnectorEnrollment Enrollment { get; }
    internal GroupIngressPayload Payload { get; }
    internal GroupListenerLeaseSnapshot Lease { get; }
    internal DateTimeOffset AdmittedAtUtc { get; }
    public GroupScope Source => Enrollment.Source.Scope;

    public static GroupConnectorSpoolAdmission Filter(GroupConnectorEnrollment enrollment, GroupIngressPayload payload,
        GroupListenerLeaseSnapshot lease, DateTimeOffset nowUtc, GroupIngressRuntimePolicy policy)
    {
        // Reject these before content validation, serialization, keys or storage.
        if (payload is null || !payload.IsGroup || payload.IsSelf || payload.IsKnownReportEcho) throw Denied();
        if (enrollment?.Source is null || payload.Event is null || lease?.Account is null || policy is null) throw Denied();
        RequireCurrent(enrollment, lease, nowUtc, policy, payload.Event.Kind);
        GroupRoutingPolicy.AuthorizeIngest(enrollment.Authentication, enrollment.Principal, enrollment.Grant,
            enrollment.Source, payload.Event.Identity, payload.IsGroup, payload.IsKnownReportEcho);
        if (lease.OwnerId != payload.ListenerOwnerId || lease.Epoch != payload.ListenerEpoch) throw Denied();
        payload.Event.Validate();
        if (payload.Text is null || payload.Text.Length > GroupSourceContentProtector.MaximumTextLength ||
            payload.Event.Kind == GroupSourceEventKind.Recall && payload.Text.Length != 0) throw Denied();
        byte[] clear;
        try { clear = new UTF8Encoding(false, true).GetBytes(payload.Text); }
        catch (EncoderFallbackException) { throw Denied(); }
        try { if (Convert.ToHexString(SHA256.HashData(clear)) != payload.Event.ContentSha256) throw Denied(); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        return new(enrollment, payload, lease, nowUtc);
    }

    // Used before decrypting recovered content, including lease/qualification
    // revocation. It cannot replace fresh backend checks at SQL commit.
    internal static void RequireCurrent(GroupConnectorEnrollment enrollment,
        GroupListenerLeaseSnapshot lease, DateTimeOffset nowUtc, GroupIngressRuntimePolicy policy, GroupSourceEventKind kind)
    {
        if (lease?.Account is null) throw Denied();
        RequireQualifiedEnrollment(enrollment, nowUtc, policy, kind);
        var source = enrollment.Source;
        if (lease.Account != new GroupListenerAccountScope(source.Scope.TenantId, source.Scope.CompanyId, source.ConnectorAccountId) ||
            lease.OwnerId == Guid.Empty || lease.Epoch <= 0 ||
            nowUtc.Offset != TimeSpan.Zero || lease.HeartbeatAtUtc.Offset != TimeSpan.Zero || lease.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            lease.HeartbeatAtUtc > nowUtc || lease.ExpiresAtUtc <= nowUtc || lease.ExpiresAtUtc - lease.HeartbeatAtUtc > GroupListenerLeasePolicy.LeaseDuration)
            throw Denied();
    }

    // Initial listener Acquire has no lease yet, but still needs the same
    // current scoped receive qualification as an admitted event.
    internal static void RequireQualifiedEnrollment(GroupConnectorEnrollment enrollment, DateTimeOffset nowUtc,
        GroupIngressRuntimePolicy policy, GroupSourceEventKind kind = GroupSourceEventKind.NewText)
    {
        if (enrollment?.Source is null || policy is null || !Enum.IsDefined(kind) || nowUtc.Offset != TimeSpan.Zero) throw Denied();
        var source = enrollment.Source;
        GroupRoutingPolicy.AuthorizeIngest(enrollment.Authentication, enrollment.Principal, enrollment.Grant,
            source, source.ExternalIdentity, isGroup: true, isKnownReportEcho: false);
        var qualification = enrollment.Qualification;
        if (qualification is null || enrollment.Artifact is null || qualification.Artifact != enrollment.Artifact ||
            qualification.TenantId != source.Scope.TenantId || qualification.CompanyId != source.Scope.CompanyId ||
            qualification.ConnectorAccountId != source.ConnectorAccountId || qualification.ExternalAccountId != source.ExternalIdentity.AccountId ||
            enrollment.Artifact.Provider != source.ExternalIdentity.Provider ||
            (policy.IsSyntheticFixture ? enrollment.Artifact.Provider != "synthetic" || enrollment.Artifact.PackageVersion != "owned-fixture" ||
                qualification.Environment != GroupQualificationEnvironment.Synthetic :
                enrollment.Artifact.Provider == "synthetic" || enrollment.Artifact.PackageVersion == "owned-fixture" ||
                !qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, source.Scope.TenantId, source.Scope.CompanyId,
                    source.ConnectorAccountId, source.ExternalIdentity.AccountId, enrollment.Artifact, nowUtc))) throw Denied();
        if (kind is GroupSourceEventKind.Edit or GroupSourceEventKind.Recall)
        {
            var capability = kind == GroupSourceEventKind.Edit ? GroupConnectorCapability.EditEvents : GroupConnectorCapability.RecallEvents;
            if (!qualification.Observations.Any(x => x.Capability == capability && x.Support == GroupConnectorSupport.Supported &&
                x.EvidenceId != Guid.Empty && x.ObservedAtUtc <= nowUtc && nowUtc - x.ObservedAtUtc <= GroupConnectorQualification.MaximumObservationAge)) throw Denied();
        }
    }

    internal static UnauthorizedAccessException Denied() => new("Connector spool admission is unavailable.");
}
