namespace MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

public enum GroupConnectorCapability
{
    GroupTextReceive = 1, MessageIdentity = 2, SenderIdentity = 3, ReplyIdentity = 4,
    SelfOriginCorrelation = 5, ListenerCollisionDetection = 6, GapDetection = 7,
    EditEvents = 8, RecallEvents = 9, MembershipVerification = 10,
    GroupTextSend = 11, ProviderAcceptanceReference = 12, UnknownSendReconciliation = 13
}

public enum GroupConnectorSupport { Unverified = 0, Supported = 1, Unsupported = 2 }
public enum GroupQualificationEnvironment { Synthetic = 1, ControlledAccount = 2 }
public enum GroupConnectorProfile { Receive = 1, Send = 2 }

public sealed record GroupConnectorObservation(GroupConnectorCapability Capability,
    GroupConnectorSupport Support, Guid EvidenceId, DateTimeOffset ObservedAtUtc);

public sealed record GroupConnectorArtifact(string Provider, string PackageVersion, string GitCommit)
{
    public void Validate()
    {
        new GroupExternalIdentity(Provider, "validation", "validation").Validate();
        if (string.IsNullOrEmpty(PackageVersion) || PackageVersion.Length > 64 ||
            PackageVersion.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '+') ||
            GitCommit is null || GitCommit.Length != 40 || GitCommit.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
            throw new InvalidOperationException("Connector artifact is invalid.");
    }
}

/// <summary>
/// Controlled observation metadata from the trusted qualification registry.
/// Documentation and synthetic observations cannot qualify a live account.
/// </summary>
public sealed class GroupConnectorQualification
{
    public static readonly TimeSpan MaximumObservationAge = TimeSpan.FromDays(30);
    private static readonly GroupConnectorCapability[] ReceiveRequirements =
    [GroupConnectorCapability.GroupTextReceive, GroupConnectorCapability.MessageIdentity,
        GroupConnectorCapability.SenderIdentity, GroupConnectorCapability.SelfOriginCorrelation,
        GroupConnectorCapability.ListenerCollisionDetection, GroupConnectorCapability.GapDetection,
        GroupConnectorCapability.MembershipVerification];
    private static readonly GroupConnectorCapability[] SendRequirements =
    [GroupConnectorCapability.GroupTextSend, GroupConnectorCapability.MembershipVerification,
        GroupConnectorCapability.ProviderAcceptanceReference, GroupConnectorCapability.UnknownSendReconciliation];

    public GroupConnectorQualification(Guid tenantId, Guid companyId, Guid connectorAccountId,
        string externalAccountId, GroupConnectorArtifact artifact,
        GroupQualificationEnvironment environment, IReadOnlyList<GroupConnectorObservation> observations)
    {
        if (tenantId == Guid.Empty || companyId == Guid.Empty || connectorAccountId == Guid.Empty ||
            artifact is null || !Enum.IsDefined(environment) || observations is null || observations.Count > 13)
            throw new InvalidOperationException("Connector qualification is invalid.");
        GroupExternalIdentity.ValidateOpaqueId(externalAccountId);
        artifact.Validate();
        var copy = observations.ToArray();
        if (copy.Any(x => x is null || !Enum.IsDefined(x.Capability) || !Enum.IsDefined(x.Support) ||
                x.ObservedAtUtc.Offset != TimeSpan.Zero ||
                (x.Support != GroupConnectorSupport.Unverified && x.EvidenceId == Guid.Empty) ||
                (x.Support == GroupConnectorSupport.Unverified && x.EvidenceId != Guid.Empty)) ||
            copy.Select(x => x.Capability).Distinct().Count() != copy.Length)
            throw new InvalidOperationException("Connector observations are invalid.");
        TenantId = tenantId;
        CompanyId = companyId;
        ConnectorAccountId = connectorAccountId;
        ExternalAccountId = externalAccountId;
        Artifact = artifact;
        Environment = environment;
        Observations = Array.AsReadOnly(copy.OrderBy(x => x.Capability).ToArray());
    }

    public Guid TenantId { get; }
    public Guid CompanyId { get; }
    public Guid ConnectorAccountId { get; }
    public string ExternalAccountId { get; }
    public GroupConnectorArtifact Artifact { get; }
    public GroupQualificationEnvironment Environment { get; }
    public IReadOnlyList<GroupConnectorObservation> Observations { get; }

    public bool AllowsLiveProfile(GroupConnectorProfile profile, Guid tenantId, Guid companyId,
        Guid connectorAccountId, string externalAccountId, GroupConnectorArtifact expectedArtifact,
        DateTimeOffset nowUtc)
    {
        if (!Enum.IsDefined(profile) || expectedArtifact is null || nowUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Connector profile is invalid.");
        expectedArtifact.Validate();
        GroupExternalIdentity.ValidateOpaqueId(externalAccountId);
        if (Environment != GroupQualificationEnvironment.ControlledAccount || tenantId != TenantId ||
            companyId != CompanyId || connectorAccountId != ConnectorAccountId ||
            !string.Equals(externalAccountId, ExternalAccountId, StringComparison.Ordinal) || expectedArtifact != Artifact)
            return false;
        var required = profile == GroupConnectorProfile.Receive ? ReceiveRequirements : SendRequirements;
        return required.All(capability => Observations.Any(x => x.Capability == capability &&
            x.Support == GroupConnectorSupport.Supported && x.EvidenceId != Guid.Empty &&
            x.ObservedAtUtc <= nowUtc && nowUtc - x.ObservedAtUtc <= MaximumObservationAge));
    }
}
