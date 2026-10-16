using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Fixed connector-host source selection, never a credential/key selector.
public sealed record GroupConnectorEnrollmentRequest(GroupScope Source, GroupExternalIdentity Identity)
{
    internal void Validate()
    {
        if (Source is null || Identity is null) throw GroupServiceDirectory.Denied();
        Source.Validate(); Identity.Validate();
    }
}

// Metadata only. The backend constructs this from final current rows while
// holding its owned Serializable authority transaction; no private content or
// credential reference is returned. Qualifications use an explicit wire DTO.
public sealed record GroupConnectorEnrollmentSnapshot(GroupServiceAuthentication Authentication,
    GroupServicePrincipal Principal, GroupServiceGrant Grant, SourceGroupBinding Source,
    GroupConnectorArtifact Artifact, GroupQualificationEnvironment Environment,
    IReadOnlyList<GroupConnectorObservation> Observations, long AccountVersion, DateTimeOffset CheckedAtUtc)
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(10);

    internal GroupConnectorEnrollment RequireCurrent(GroupConnectorEnrollmentRequest request,
        GroupConnectorSigningBinding binding, DateTimeOffset nowUtc, GroupIngressRuntimePolicy policy)
    {
        if (request is null || binding is null || Source?.ExternalIdentity is null || Authentication is null ||
            Source.Scope != request.Source || Source.ExternalIdentity != request.Identity ||
            Source.Scope.TenantId != binding.TenantId || Source.Scope.CompanyId != binding.CompanyId ||
            Authentication.ServiceId != binding.ServiceId || Authentication.CredentialEpoch != binding.CredentialEpoch ||
            AccountVersion <= 0 || CheckedAtUtc.Offset != TimeSpan.Zero || nowUtc.Offset != TimeSpan.Zero ||
            CheckedAtUtc > nowUtc || nowUtc - CheckedAtUtc > MaximumAge) throw GroupServiceDirectory.Denied();
        var qualification = new GroupConnectorQualification(Source.Scope.TenantId, Source.Scope.CompanyId,
            Source.ConnectorAccountId, Source.ExternalIdentity.AccountId, Artifact, Environment, Observations);
        var enrollment = new GroupConnectorEnrollment(Authentication, Principal, Grant, Source, qualification, Artifact);
        GroupConnectorSpoolAdmission.RequireQualifiedEnrollment(enrollment, nowUtc, policy);
        return enrollment;
    }

    internal static GroupConnectorEnrollmentSnapshot FromCurrent(GroupIngestAuthority current,
        GroupConnectorQualification qualification, DateTimeOffset checkedAtUtc) => new(
        new(current.Principal.ServiceId, current.Principal.CredentialEpoch), current.Principal, current.Grant,
        current.Source, qualification.Artifact, qualification.Environment, qualification.Observations,
        current.Account.Version, checkedAtUtc);
}

public sealed class GroupConnectorCurrentEnrollment
{
    internal GroupConnectorCurrentEnrollment(GroupConnectorEnrollment enrollment, DateTimeOffset checkedAtUtc)
    { Enrollment = enrollment; CheckedAtUtc = checkedAtUtc; }
    public GroupConnectorEnrollment Enrollment { get; }
    public DateTimeOffset CheckedAtUtc { get; }
}
