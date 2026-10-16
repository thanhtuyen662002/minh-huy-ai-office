namespace MinhHuy.AIOffice.Platform.Persistence;

// Append-only, quarantined private output. Never serialize an EF row into
// logs, API responses, reports or provider requests.
public sealed class GroupModelAttemptResultRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid RequestId { get; set; }
    public Guid BatchId { get; set; }
    public string PlanSha256 { get; set; } = "";
    public string InputSha256 { get; set; } = "";
    public int ChunkOrdinal { get; set; }
    public Guid OperationId { get; set; }
    public int FormatVersion { get; set; }
    public string ProtectedResultSha256 { get; set; } = "";
    public string EncryptionKeyId { get; set; } = "";
    public byte[] ProtectedResult { get; set; } = [];
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset PersistedAtUtc { get; set; }
    public override string ToString() => "Group model attempt result (private SQL record).";
}
