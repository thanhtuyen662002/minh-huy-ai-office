namespace MinhHuy.AIOffice.Platform.Persistence;

// Append-only metadata. Provider input, response bodies and credentials are
// deliberately absent. Current authority still belongs to the live SQL fence.
public sealed class GroupModelAttemptStartRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid RequestId { get; set; }
    public Guid BatchId { get; set; }
    public string InputSha256 { get; set; } = "";
    public int Ordinal { get; set; }
    public int? PredecessorOrdinal { get; set; }
    public Guid? PredecessorRequestId { get; set; }
    public Guid ClaimOperationId { get; set; }
    public long ClaimEpoch { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public override string ToString() => "Group model attempt start metadata.";
}

public sealed class GroupModelAttemptFailureRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid RequestId { get; set; }
    public int Failure { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public override string ToString() => "Group model attempt failure metadata.";
}
