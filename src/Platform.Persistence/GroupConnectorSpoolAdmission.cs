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
    private GroupConnectorSpoolAdmission(GroupConnectorEnrollment enrollment, GroupIngressPayload payload, DateTimeOffset admittedAtUtc)
    { Enrollment = enrollment; Payload = payload; AdmittedAtUtc = admittedAtUtc; }
    internal GroupConnectorEnrollment Enrollment { get; }
    internal GroupIngressPayload Payload { get; }
    internal DateTimeOffset AdmittedAtUtc { get; }
    public GroupScope Source => Enrollment.Source.Scope;

    public static GroupConnectorSpoolAdmission Filter(GroupConnectorEnrollment enrollment, GroupIngressPayload payload,
        GroupListenerLeaseSnapshot lease, DateTimeOffset nowUtc, GroupIngressRuntimePolicy policy)
    {
        // Reject these before content validation, serialization, keys or storage.
        if (payload is null || !payload.IsGroup || payload.IsSelf || payload.IsKnownReportEcho) throw Denied();
        if (enrollment?.Source is null || payload.Event is null || lease?.Account is null || policy is null) throw Denied();
        RequireCurrent(enrollment, lease, nowUtc, policy);
        GroupRoutingPolicy.AuthorizeIngest(enrollment.Authentication, enrollment.Principal, enrollment.Grant,
            enrollment.Source, payload.Event.Identity, payload.IsGroup, payload.IsKnownReportEcho);
        if (lease.OwnerId != payload.ListenerOwnerId || lease.Epoch != payload.ListenerEpoch) throw Denied();
        var qualification = enrollment.Qualification;
        if (payload.Event.Kind is GroupSourceEventKind.Edit or GroupSourceEventKind.Recall)
        {
            var capability = payload.Event.Kind == GroupSourceEventKind.Edit ? GroupConnectorCapability.EditEvents : GroupConnectorCapability.RecallEvents;
            if (!qualification.Observations.Any(x => x.Capability == capability && x.Support == GroupConnectorSupport.Supported &&
                x.EvidenceId != Guid.Empty && x.ObservedAtUtc <= nowUtc && nowUtc - x.ObservedAtUtc <= GroupConnectorQualification.MaximumObservationAge)) throw Denied();
        }
        payload.Event.Validate();
        if (payload.Text is null || payload.Text.Length > GroupSourceContentProtector.MaximumTextLength ||
            payload.Event.Kind == GroupSourceEventKind.Recall && payload.Text.Length != 0) throw Denied();
        byte[] clear;
        try { clear = new UTF8Encoding(false, true).GetBytes(payload.Text); }
        catch (EncoderFallbackException) { throw Denied(); }
        try { if (Convert.ToHexString(SHA256.HashData(clear)) != payload.Event.ContentSha256) throw Denied(); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        return new(enrollment, payload, nowUtc);
    }

    // Used before decrypting recovered content, including lease/qualification
    // revocation. It cannot replace fresh backend checks at SQL commit.
    internal static void RequireCurrent(GroupConnectorEnrollment enrollment,
        GroupListenerLeaseSnapshot lease, DateTimeOffset nowUtc, GroupIngressRuntimePolicy policy)
    {
        if (enrollment?.Source is null || lease?.Account is null || policy is null) throw Denied();
        var source = enrollment.Source;
        GroupRoutingPolicy.AuthorizeIngest(enrollment.Authentication, enrollment.Principal, enrollment.Grant,
            source, source.ExternalIdentity, isGroup: true, isKnownReportEcho: false);
        if (lease.Account != new GroupListenerAccountScope(source.Scope.TenantId, source.Scope.CompanyId, source.ConnectorAccountId) ||
            lease.OwnerId == Guid.Empty || lease.Epoch <= 0 ||
            nowUtc.Offset != TimeSpan.Zero || lease.HeartbeatAtUtc.Offset != TimeSpan.Zero || lease.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            lease.HeartbeatAtUtc > nowUtc || lease.ExpiresAtUtc <= nowUtc || lease.ExpiresAtUtc - lease.HeartbeatAtUtc > GroupListenerLeasePolicy.LeaseDuration)
            throw Denied();
        var qualification = enrollment.Qualification;
        if (qualification is null || enrollment.Artifact is null || qualification.Artifact != enrollment.Artifact ||
            qualification.TenantId != source.Scope.TenantId || qualification.CompanyId != source.Scope.CompanyId ||
            qualification.ConnectorAccountId != source.ConnectorAccountId || qualification.ExternalAccountId != source.ExternalIdentity.AccountId ||
            enrollment.Artifact.Provider != source.ExternalIdentity.Provider ||
            (policy.IsSyntheticFixture ? qualification.Environment != GroupQualificationEnvironment.Synthetic :
                !qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, source.Scope.TenantId, source.Scope.CompanyId,
                    source.ConnectorAccountId, source.ExternalIdentity.AccountId, enrollment.Artifact, nowUtc))) throw Denied();
    }

    internal static UnauthorizedAccessException Denied() => new("Connector spool admission is unavailable.");
}
