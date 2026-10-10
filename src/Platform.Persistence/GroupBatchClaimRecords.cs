using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// The identity columns remain immutable. Only the reviewed lease fields may
// advance under the source transaction lock. Expiry is never completion.
public sealed class GroupBatchClaimStateRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public long Epoch { get; set; }
    public Guid OwnerId { get; set; }
    public Guid OperationId { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

// Original acquisition nonce is an immutable receipt. Replaying it does not
// renew a lease, even for the same owner or after process replacement.
public sealed class GroupBatchClaimReceiptRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid OperationId { get; set; }
    public Guid BatchId { get; set; }
    public Guid OwnerId { get; set; }
    public long Epoch { get; set; }
    public long RequestedLifetimeTicks { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public Guid ServiceId { get; set; }
    public long CredentialEpoch { get; set; }
    public long GrantVersion { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public long AccountVersion { get; set; }
    public string AuthoritySha256 { get; set; } = "";
}

public sealed record GroupBatchClaimReceipt(GroupScope Scope, Guid OperationId, Guid BatchId, Guid OwnerId,
    long Epoch, long RequestedLifetimeTicks, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc,
    Guid ServiceId, long CredentialEpoch, long GrantVersion, long SourceVersion, long DeletionGeneration,
    long AccountVersion);

// A caller/broker/model DTO cannot construct a verified handle. Every read or
// final mutation must still validate its live SQL lease and current authority.
public sealed class GroupBatchClaimHandle
{
    internal GroupBatchClaimHandle(GroupBatchClaimReceipt receipt, GroupExtractionAuthority authority)
    { Receipt = receipt; Authority = authority; }
    public GroupBatchClaimReceipt Receipt { get; }
    internal GroupExtractionAuthority Authority { get; }
}

public sealed record GroupBatchClaimResult(GroupBatchClaimReceipt Receipt,
    GroupBatchClaimHandle? CurrentHandle, bool WasAlreadyClaimed);
