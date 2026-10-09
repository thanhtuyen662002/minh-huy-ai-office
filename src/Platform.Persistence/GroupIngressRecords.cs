using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public enum GroupBindingRole { CustomerSource = 1, TechnicalInternal = 2 }

// Operator-owned registry rows. Runtime can read them but cannot enroll itself,
// change a role, renew qualification or grant a capability.
public sealed class GroupConnectorAccountRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalAccountId { get; set; } = "";
    public string IdentityHash { get; set; } = "";
    public string PackageVersion { get; set; } = "";
    public string GitCommit { get; set; } = "";
    public string QualificationJson { get; set; } = "";
    public long Version { get; set; } = 1;
    public bool IsEnabled { get; set; }
}

public sealed class GroupServiceRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public long CredentialEpoch { get; set; } = 1;
    public string CredentialReference { get; set; } = "";
    public bool IsEnabled { get; set; }
}

// One physical provider/group can have exactly one registered role, even if
// a second account or company tries to alias it. Hashes only index candidates;
// runtime still compares all original strings ordinally before authorizing.
public sealed class GroupBindingRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ConnectorAccountId { get; set; }
    public GroupBindingRole Role { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalAccountId { get; set; } = "";
    public string ExternalGroupId { get; set; } = "";
    public string IdentityHash { get; set; } = "";
    public string PhysicalGroupHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long Version { get; set; } = 1;
    public long DeletionGeneration { get; set; }
    public bool IsEnabled { get; set; }
}

public sealed class GroupServiceGrantRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid BindingId { get; set; }
    public GroupServiceCapability Capability { get; set; }
    public long Version { get; set; } = 1;
    public bool IsEnabled { get; set; }
}

// Read permission is distinct from service capability and IT disclosure.
public sealed class GroupReaderGrantRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public Guid BindingId { get; set; }
    public long Version { get; set; } = 1;
    public bool IsEnabled { get; set; }
}

public sealed class GroupListenerLeaseRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ConnectorAccountId { get; set; }
    public Guid OwnerId { get; set; }
    public long Epoch { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset HeartbeatAtUtc { get; set; }
}

// Incremented while holding the source transaction lock through commit.
// Identity allocation / MAX(identity) is never a committed-ingest cursor.
public sealed class GroupSourceStateRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public long CommittedSequence { get; set; }
    public DateTimeOffset? FirstPendingAtUtc { get; set; }
    public DateTimeOffset? LastPendingAtUtc { get; set; }
    public long ScheduledThroughSequence { get; set; }
}

public sealed class GroupMessageRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid Id { get; set; }
    public string ExternalMessageId { get; set; } = "";
    public string IdentityHash { get; set; } = "";
}

// Protected original source is stored here, not in a context manifest/checkpoint.
// Revisions and receipts are append-only under runtime SQL permissions.
public sealed class GroupMessageRevisionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid MessageId { get; set; }
    public long Revision { get; set; }
    public long CommittedSequence { get; set; }
    public string ExternalRevisionEventId { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string? ReplyToMessageId { get; set; }
    public GroupSourceEventKind Kind { get; set; }
    public string ContentSha256 { get; set; } = "";
    public string ContentKeyId { get; set; } = "";
    public byte[] ProtectedContent { get; set; } = [];
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
    public bool IsHistoricalBackfill { get; set; }
}

public sealed class GroupIngressReceiptRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public string EventIdentityHash { get; set; } = "";
    public string ExternalRevisionEventId { get; set; } = "";
    public string EnvelopeSha256 { get; set; } = "";
    public Guid MessageId { get; set; }
    public long Revision { get; set; }
    public Guid ServiceId { get; set; }
    public long CredentialEpoch { get; set; }
    public long ListenerEpoch { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
}

public sealed class GroupCoverageGapRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid Id { get; set; }
    public long AfterCommittedSequence { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset OpenedAtUtc { get; set; }
    public DateTimeOffset? ReconnectedAtUtc { get; set; }
}

// Reference-only publication, atomically committed with receipt/revision.
public sealed class GroupIngressOutboxRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public long Revision { get; set; }
    public long CommittedSequence { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int PublishAttempts { get; set; }
}
