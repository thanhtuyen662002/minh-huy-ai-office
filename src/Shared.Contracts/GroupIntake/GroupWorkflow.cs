using System.Text;
using System.Text.Json.Serialization;

namespace MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

public enum GroupCoverage { Complete = 1, PartialGap = 2, PartialMedia = 3 }
public enum GroupNoteKind { Incident = 1, ChangeRequest = 2, Question = 3, NeedsClarification = 4, ExtractionFailed = 5 }
public enum GroupNoteBusinessStatus { New = 1, Open = 2, NeedsClarification = 3, Resolved = 4, Closed = 5 }
public enum GroupReportSendState { Pending = 1, Dispatching = 2, Accepted = 3, Failed = 4, Unknown = 5, Blocked = 6 }

public sealed record GroupMessageRevisionReference(Guid MessageId, long Revision,
    long CommittedSequence, string ContentSha256);

public sealed class FrozenGroupBatch
{
    private FrozenGroupBatch(Guid id, GroupScope scope, long cutoff, GroupCoverage coverage,
        bool historicalBackfill, IReadOnlyList<GroupMessageRevisionReference> messages)
    {
        Id = id;
        Scope = scope;
        CommittedCutoff = cutoff;
        Coverage = coverage;
        IsHistoricalBackfill = historicalBackfill;
        Messages = messages;
    }
    public const int MaximumMessages = 100;
    public Guid Id { get; }
    public GroupScope Scope { get; }
    public long CommittedCutoff { get; }
    public GroupCoverage Coverage { get; }
    public bool IsHistoricalBackfill { get; }
    public bool MayNotify => !IsHistoricalBackfill;
    public IReadOnlyList<GroupMessageRevisionReference> Messages { get; }

    /// <summary>Structural freeze after SQL proves these exact revisions committed in this scope.</summary>
    public static FrozenGroupBatch Create(Guid id, GroupScope scope, long committedCutoff,
        GroupCoverage coverage, bool historicalBackfill, IReadOnlyList<GroupMessageRevisionReference> messages)
    {
        if (id == Guid.Empty || scope is null || committedCutoff <= 0 || !Enum.IsDefined(coverage) ||
            messages is null || messages.Count is < 1 or > MaximumMessages)
            throw new InvalidOperationException("Group batch is invalid.");
        scope.Validate();
        var copy = messages.ToArray();
        foreach (var message in copy)
        {
            if (message is null || message.MessageId == Guid.Empty || message.Revision <= 0 ||
                message.CommittedSequence <= 0 || message.CommittedSequence > committedCutoff)
                throw new InvalidOperationException("Group batch revision is invalid.");
            GroupWorkflowValidation.RequireSha256(message.ContentSha256);
        }
        if (copy.Select(x => x.MessageId).Distinct().Count() != copy.Length ||
            copy.Select(x => x.CommittedSequence).Distinct().Count() != copy.Length)
            throw new InvalidOperationException("Group batch revisions are ambiguous.");
        return new(id, scope, committedCutoff, coverage, historicalBackfill,
            Array.AsReadOnly(copy.OrderBy(x => x.CommittedSequence).ToArray()));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupProposalEvidence(Guid MessageId, long Revision, string? Quote);

/// <summary>Model proposal only: no tenant, authority, destination, assignee or committed status.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupNoteProposal(string CandidateKey, GroupNoteKind Kind, string Title,
    string ReportedProblem, string? RequestedOutcome, IReadOnlyList<GroupProposalEvidence> Evidence,
    IReadOnlyList<string> MissingFields, string? RequestedDeadlineText = null,
    string? SuggestedRelatedRequestCode = null)
{
    // This is structural validation only. SQL/worker must resolve the exact source
    // revision and validate quotes/facts/related-note scope before automatic commit.
    public void Validate(FrozenGroupBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        GroupWorkflowValidation.RequireText(CandidateKey, 64);
        if (CandidateKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            !Enum.IsDefined(Kind) || Kind == GroupNoteKind.ExtractionFailed)
            throw new InvalidOperationException("Note proposal is invalid.");
        GroupWorkflowValidation.RequireText(Title, 200);
        GroupWorkflowValidation.RequireText(ReportedProblem, 2000);
        GroupWorkflowValidation.OptionalText(RequestedOutcome, 2000);
        GroupWorkflowValidation.OptionalText(RequestedDeadlineText, 200);
        GroupWorkflowValidation.OptionalText(SuggestedRelatedRequestCode, 64);
        if (Evidence is null || Evidence.Count is < 1 or > 16 || MissingFields is null || MissingFields.Count > 16)
            throw new InvalidOperationException("Note proposal evidence is invalid.");
        var seen = new HashSet<(Guid, long)>();
        foreach (var evidence in Evidence)
        {
            if (evidence is null || !seen.Add((evidence.MessageId, evidence.Revision)) ||
                !batch.Messages.Any(x => x.MessageId == evidence.MessageId && x.Revision == evidence.Revision))
                throw new InvalidOperationException("Note proposal evidence is outside the frozen batch.");
            if (evidence.Quote is null)
            {
                if (Kind != GroupNoteKind.NeedsClarification)
                    throw new InvalidOperationException("Reported facts require source quotes.");
            }
            else GroupWorkflowValidation.RequireText(evidence.Quote, 1000);
        }
        foreach (var field in MissingFields) GroupWorkflowValidation.RequireText(field, 200);
        if (MissingFields.Distinct(StringComparer.Ordinal).Count() != MissingFields.Count ||
            (Kind == GroupNoteKind.NeedsClarification && MissingFields.Count == 0))
            throw new InvalidOperationException("Note clarification fields are invalid.");
    }

    public GroupNoteBusinessStatus GetInitialBusinessStatus() =>
        Kind == GroupNoteKind.NeedsClarification ? GroupNoteBusinessStatus.NeedsClarification : GroupNoteBusinessStatus.New;
}

public sealed record GroupBatchTiming(TimeSpan QuietPeriod, TimeSpan MaximumWait)
{
    // Initial configurable tuning, not an owner-mandated cadence or an in-memory timer.
    public static readonly GroupBatchTiming InitialTuning = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));

    public DateTimeOffset ComputeDue(DateTimeOffset firstUnprocessedCommitUtc, DateTimeOffset latestCommitUtc)
    {
        if (QuietPeriod < TimeSpan.FromSeconds(1) || MaximumWait < QuietPeriod || MaximumWait > TimeSpan.FromDays(1) ||
            firstUnprocessedCommitUtc.Offset != TimeSpan.Zero || latestCommitUtc.Offset != TimeSpan.Zero ||
            latestCommitUtc < firstUnprocessedCommitUtc)
            throw new InvalidOperationException("Group batch timing is invalid.");
        try
        {
            var maximumDue = firstUnprocessedCommitUtc + MaximumWait;
            var quietDue = latestCommitUtc + QuietPeriod;
            return maximumDue <= quietDue ? maximumDue : quietDue;
        }
        catch (ArgumentOutOfRangeException) { throw new InvalidOperationException("Group batch timing is invalid."); }
    }
}

public sealed record GroupCommittedNoteReference(Guid NoteId, long Revision, string RequestCode);

/// <summary>Outbox references created atomically with notes; no raw messages in the broker.</summary>
public sealed class GroupNotesCommitted
{
    private GroupNotesCommitted(Guid eventId, FrozenGroupBatch batch, IReadOnlyList<GroupCommittedNoteReference> notes)
    {
        EventId = eventId;
        BatchId = batch.Id;
        Scope = batch.Scope;
        Coverage = batch.Coverage;
        MayNotify = batch.MayNotify;
        Notes = notes;
    }
    public Guid EventId { get; }
    public Guid BatchId { get; }
    public GroupScope Scope { get; }
    public GroupCoverage Coverage { get; }
    public bool MayNotify { get; }
    public IReadOnlyList<GroupCommittedNoteReference> Notes { get; }

    public static GroupNotesCommitted Create(Guid eventId, FrozenGroupBatch batch,
        IReadOnlyList<GroupCommittedNoteReference> notes)
    {
        if (eventId == Guid.Empty || batch is null || notes is null || notes.Count is < 1 or > 100)
            throw new InvalidOperationException("Committed note references are invalid.");
        var copy = notes.ToArray();
        foreach (var note in copy)
        {
            if (note is null || note.NoteId == Guid.Empty || note.Revision <= 0)
                throw new InvalidOperationException("Committed note reference is invalid.");
            GroupWorkflowValidation.RequireText(note.RequestCode, 64);
        }
        if (copy.Select(x => x.NoteId).Distinct().Count() != copy.Length ||
            copy.Select(x => x.RequestCode).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new InvalidOperationException("Committed note references are ambiguous.");
        return new(eventId, batch, Array.AsReadOnly(copy.OrderBy(x => x.NoteId).ToArray()));
    }
}

public static class GroupReportSendPolicy
{
    // An actual SQL compare-and-set must claim Pending -> Dispatching once.
    // Failed/Blocked rebuilds require a new validated version; Unknown is never
    // a retry signal because the provider may already have accepted the message.
    public static bool CanClaim(GroupReportSendState state)
    {
        if (!Enum.IsDefined(state)) throw new InvalidOperationException("Report state is invalid.");
        return state == GroupReportSendState.Pending;
    }
}

internal static class GroupWorkflowValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static void RequireSha256(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= 'A' and <= 'F') and not (>= '0' and <= '9')))
            throw new InvalidOperationException("Group revision digest is invalid.");
    }
    internal static void OptionalText(string? value, int maximumLength)
    {
        if (value is not null) RequireText(value, maximumLength);
    }
    internal static void RequireText(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new InvalidOperationException("Group text is invalid.");
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new InvalidOperationException("Group text is invalid."); }
    }
}
