using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public enum GroupRequestRevisionOrigin { AiExtracted = 1, ItEdited = 2, HostAttention = 3 }
public enum GroupRequestVerificationLevel { SourceBackedAiInterpretation = 1, ItConfirmed = 2, HostObserved = 3 }
public enum GroupRequestEvidenceKind { LiteralSourceQuote = 1, HostMetadataAttention = 2 }
public enum GroupWorkCommitOutcome { Notes = 1, NoWork = 2, Attention = 3 }
public enum GroupWorkSourceOutcome
{ Work = 1, NoWork = 2, Quarantined = 3, Recalled = 4, ObsoleteGeneration = 5, ChangedAfterCutoff = 6, Attention = 7, ExtractionFailed = 8 }

// Business heads contain metadata only. CurrentRevision points into protected
// immutable revisions; model/runtime completion cannot confirm IT state.
public sealed class GroupCustomerRequestRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid Id { get; set; }
    public Guid OriginBatchId { get; set; }
    public Guid OriginOperationId { get; set; }
    public int OriginCandidateOrdinal { get; set; }
    public string RequestCode { get; set; } = "";
    public GroupNoteKind Kind { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public long CurrentRevision { get; set; } = 1;
    public GroupNoteBusinessStatus BusinessStatus { get; set; } = GroupNoteBusinessStatus.New;
    public long BusinessVersion { get; set; } = 1;
    public Guid? AssignedToUserId { get; set; }
    public DateTimeOffset? CommittedDueAtUtc { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

// All interpretations, customer deadline wording and evidence quote text live
// inside the brain-purpose AEAD payload; no plaintext summary/hash is stored.
public sealed class GroupRequestRevisionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid RequestId { get; set; }
    public long Revision { get; set; }
    public GroupRequestRevisionOrigin Origin { get; set; }
    public GroupRequestVerificationLevel VerificationLevel { get; set; }
    public Guid? AuthorServiceId { get; set; }
    public Guid? AuthorUserId { get; set; }
    public Guid? SourceBatchId { get; set; }
    public long? ClaimEpoch { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public string ContentKeyId { get; set; } = "";
    public byte[] ProtectedContent { get; set; } = [];
    public string EnvelopeSha256 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

// Ordinal selects the evidence entry in the protected revision payload.
// A metadata-only host attention reference never implies a literal quote.
public sealed class GroupRequestEvidenceRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid RequestId { get; set; }
    public long RequestRevision { get; set; }
    public int Ordinal { get; set; }
    public Guid MessageId { get; set; }
    public long MessageRevision { get; set; }
    public GroupRequestEvidenceKind Kind { get; set; }
}

// Immutable chunk receipt is committed with its selected-source dispositions,
// requests/revisions/evidence and outbox. It is not whole raw-batch completion.
public sealed class GroupWorkCommitReceiptRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public Guid OperationId { get; set; }
    public string SourceSetSha256 { get; set; } = "";
    public int SelectedMessageCount { get; set; }
    public int NoteCount { get; set; }
    public GroupWorkCommitOutcome Outcome { get; set; }
    public Guid ServiceId { get; set; }
    public long ClaimEpoch { get; set; }
    public long CredentialEpoch { get; set; }
    public long GrantVersion { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public long AccountVersion { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
}

// One terminal selected message per batch prevents new operation nonces from
// manufacturing unbounded duplicate requests. Raw ledger/frontier is separate.
public sealed class GroupWorkSourceDispositionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public Guid MessageId { get; set; }
    public long MessageRevision { get; set; }
    public Guid OperationId { get; set; }
    public GroupWorkSourceOutcome Outcome { get; set; }
}

// Metadata-only outbox. Sender/reporter must reread these exact protected SQL
// revisions under its own route/source/audience authorization.
public sealed class GroupNotesCommittedOutboxRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid OperationId { get; set; }
    public int NoteCount { get; set; }
    public bool IsHistoricalBackfill { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public int PublishAttempts { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

public sealed class GroupNotesCommittedItemRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid OutboxId { get; set; }
    public int Ordinal { get; set; }
    public Guid RequestId { get; set; }
    public long RequestRevision { get; set; }
}

// Operator-owned authority, distinct from GroupReaderGrants. Audience rights
// never create this grant. Future IT APIs must fence it and user membership.
public sealed class GroupEditorGrantRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid UserId { get; set; }
    public long Version { get; set; } = 1;
    public bool IsEnabled { get; set; }
}

// Operator-published exact-group glossary. Extract cannot publish/enable it.
public sealed class GroupGlossaryEntryRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid Id { get; set; }
    public long CurrentRevision { get; set; }
    public long Version { get; set; } = 1;
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public bool IsEnabled { get; set; }
    public bool AllowExtraction { get; set; }
    public Guid PublishedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class GroupGlossaryRevisionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid EntryId { get; set; }
    public long Revision { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public Guid PublishedByUserId { get; set; }
    public string ContentKeyId { get; set; } = "";
    public byte[] ProtectedContent { get; set; } = [];
    public string EnvelopeSha256 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
