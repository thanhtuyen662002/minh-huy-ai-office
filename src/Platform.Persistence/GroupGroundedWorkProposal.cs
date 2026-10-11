using System.Globalization;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public enum GroupWorkProposalKind { Incident = 1, ChangeRequest = 2, Request = 3, NeedsClarification = 4 }
public enum GroupModelSourceDisposition { Work = 1, NoWork = 2 }

public sealed class GroupProposalEvidence
{
    internal GroupProposalEvidence(Guid messageId, long revision, string quote)
    { MessageId = messageId; Revision = revision; Quote = quote; }
    public Guid MessageId { get; }
    public long Revision { get; }
    public string Quote { get; }
    public override string ToString() => "Group proposal evidence (private content).";
}

// Text interpretations remain AI proposals. Literal evidence is independently
// checked, but cannot mechanically establish the interpretation's meaning.
// There is no IT status, assignee, committed SLA, destination or authority here.
public sealed class GroupGroundedWorkNote
{
    internal GroupGroundedWorkNote(GroupWorkProposalKind kind, string title, string problem, string outcome,
        string[] missing, string? deadline, string? relation, GroupProposalEvidence[] evidence, GroupProposalEvidence? deadlineEvidence)
    {
        Kind = kind; TitleInterpretation = title; ProblemInterpretation = problem; RequestedOutcomeInterpretation = outcome;
        MissingFields = Array.AsReadOnly(missing); CustomerRequestedDeadlineText = deadline; SuggestedRelationHint = relation;
        Evidence = Array.AsReadOnly(evidence); DeadlineEvidence = deadlineEvidence;
    }
    public GroupWorkProposalKind Kind { get; }
    public string TitleInterpretation { get; }
    public string ProblemInterpretation { get; }
    public string RequestedOutcomeInterpretation { get; }
    public IReadOnlyList<string> MissingFields { get; }
    public string? CustomerRequestedDeadlineText { get; }
    public string? SuggestedRelationHint { get; }
    public IReadOnlyList<GroupProposalEvidence> Evidence { get; }
    public GroupProposalEvidence? DeadlineEvidence { get; }
    public override string ToString() => "Group grounded work note (private proposal).";
}

public sealed class GroupProposalSourceReceipt
{
    internal GroupProposalSourceReceipt(Guid messageId, long revision, GroupModelSourceDisposition disposition)
    { MessageId = messageId; Revision = revision; Disposition = disposition; }
    public Guid MessageId { get; }
    public long Revision { get; }
    public GroupModelSourceDisposition Disposition { get; }
    public override string ToString() => "Group proposal source receipt (metadata).";
}

// A validated private proposal is not a persisted note or release capability.
// The later consumer must fence this exact source context, current same-group
// note/glossary dependencies and its live claim before any SQL effects.
public sealed class GroupGroundedWorkProposal
{
    public const string FormatVersion = "group-work-proposal-v1";
    public const int MaximumOutputUtf8Bytes = 64000;
    public const int MaximumNotes = 20;
    public const int MaximumEvidence = 100;
    // Existing StructuredGeneration profile: closed required-property objects,
    // primitive nullable fields and enums. Host limits/grounding remain below.
    public const string ResponseSchema = """
        {
          "type":"object","additionalProperties":false,
          "required":["version","notes","source_dispositions"],
          "properties":{
            "version":{"type":"string","enum":["group-work-proposal-v1"]},
            "notes":{"type":"array","description":"At most20 notes and100 total evidence references, validated by the host.","items":{
              "type":"object","additionalProperties":false,
              "required":["type","title","problem","outcome","source_refs","missing_fields","requested_deadline_text","suggested_relation"],
              "properties":{
                "type":{"type":"string","enum":["incident","change_request","request","needs_clarification"]},
                "title":{"type":"string"},"problem":{"type":"string"},"outcome":{"type":"string"},
                "source_refs":{"type":"array","items":{
                  "type":"object","additionalProperties":false,"required":["message_id","revision","quote"],
                  "properties":{"message_id":{"type":"string"},"revision":{"type":"integer"},"quote":{"type":"string"}}
                }},
                "missing_fields":{"type":"array","description":"At least one missing field is required for needs_clarification; at most10 distinct nonempty fields, validated by the host.","items":{"type":"string"}},
                "requested_deadline_text":{"type":["string","null"],"description":"Customer wording literally inside supplied evidence, never an IT commitment."},
                "suggested_relation":{"type":["string","null"],"description":"Unverified hint; cannot authorize a link/update."}
              }
            }},
            "source_dispositions":{"type":"array","description":"Exactly one entry per supplied model-eligible source.","items":{
              "type":"object","additionalProperties":false,"required":["message_id","revision","disposition"],
              "properties":{"message_id":{"type":"string"},"revision":{"type":"integer"},"disposition":{"type":"string","enum":["work","no_work"]}}
            }}
          }
        }
        """;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private GroupGroundedWorkProposal(GroupBatchSourcePreparation preparation, GroupGroundedWorkNote[] notes,
        GroupProposalSourceReceipt[] dispositions)
    { Preparation = preparation; Notes = Array.AsReadOnly(notes); SourceDispositions = Array.AsReadOnly(dispositions); }
    internal GroupBatchSourcePreparation Preparation { get; }
    public GroupScope Scope => Preparation.Scope;
    public Guid BatchId => Preparation.BatchId;
    public IReadOnlyList<GroupGroundedWorkNote> Notes { get; }
    public IReadOnlyList<GroupProposalSourceReceipt> SourceDispositions { get; }
    public override string ToString() => "Group grounded work proposal (private content).";

    public static GroupGroundedWorkProposal Parse(GroupBatchSourcePreparation preparation, string? output)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        // Post-output policy applies to the entire decoded object before any
        // interpretation. Do not include offending text or decoder exceptions.
        if (GroupSecretQuarantine.InspectOutputJson(output).RequiresQuarantine) throw Unavailable();
        try
        {
            if (StrictUtf8.GetByteCount(output!) > MaximumOutputUtf8Bytes) throw Unavailable();
            using var document = JsonDocument.Parse(output!, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            Shape(root, "version", "notes", "source_dispositions");
            if (Text(root, "version", 80) != FormatVersion) throw Unavailable();
            var candidates = preparation.Candidates.ToDictionary(source => source.MessageId);
            var used = new HashSet<Guid>();
            var notes = new List<GroupGroundedWorkNote>();
            var originalNotes = new List<JsonElement>();
            var evidenceCount = 0;
            foreach (var note in Items(root.GetProperty("notes"), MaximumNotes))
            {
                Shape(note, "type", "title", "problem", "outcome", "source_refs", "missing_fields", "requested_deadline_text", "suggested_relation");
                if (originalNotes.Any(previous => JsonElement.DeepEquals(previous, note))) throw Unavailable();
                originalNotes.Add(note);
                var kind = Text(note, "type", 40) switch
                {
                    "incident" => GroupWorkProposalKind.Incident,
                    "change_request" => GroupWorkProposalKind.ChangeRequest,
                    "request" => GroupWorkProposalKind.Request,
                    "needs_clarification" => GroupWorkProposalKind.NeedsClarification,
                    _ => throw Unavailable()
                };
                var title = Text(note, "title", 200); var problem = Text(note, "problem", 2000); var outcome = Text(note, "outcome", 1000);
                var missing = Items(note.GetProperty("missing_fields"), 10).Select(item => String(item, 200)).ToArray();
                if (missing.Distinct(StringComparer.Ordinal).Count() != missing.Length
                    || (kind == GroupWorkProposalKind.NeedsClarification && missing.Length == 0)) throw Unavailable();
                var deadline = NullableText(note, "requested_deadline_text", 1000);
                var relation = NullableText(note, "suggested_relation", 200);
                var evidence = new List<GroupProposalEvidence>();
                var uniqueEvidence = new HashSet<(Guid, long, string)>();
                foreach (var reference in Items(note.GetProperty("source_refs"), 10))
                {
                    Shape(reference, "message_id", "revision", "quote");
                    var source = Source(reference, candidates);
                    var quote = Text(reference, "quote", 4000);
                    if (!source.Text.Contains(quote, StringComparison.Ordinal) || !uniqueEvidence.Add((source.MessageId, source.Revision, quote))
                        || ++evidenceCount > MaximumEvidence) throw Unavailable();
                    evidence.Add(new(source.MessageId, source.Revision, quote)); used.Add(source.MessageId);
                }
                if (evidence.Count == 0) throw Unavailable();
                // Customer wording must occur literally within validated source
                // evidence. Never parse it into a promised/resolved IT deadline.
                var deadlineEvidence = deadline is null ? null : evidence.FirstOrDefault(item => item.Quote.Contains(deadline, StringComparison.Ordinal));
                if (deadline is not null && deadlineEvidence is null) throw Unavailable();
                notes.Add(new(kind, title, problem, outcome, missing, deadline, relation, evidence.ToArray(), deadlineEvidence));
            }
            var dispositions = new List<GroupProposalSourceReceipt>();
            var seen = new HashSet<Guid>();
            foreach (var disposition in Items(root.GetProperty("source_dispositions"), 100))
            {
                Shape(disposition, "message_id", "revision", "disposition");
                var source = Source(disposition, candidates);
                if (!seen.Add(source.MessageId)) throw Unavailable();
                var parsed = Text(disposition, "disposition", 20) switch
                {
                    "work" => GroupModelSourceDisposition.Work,
                    "no_work" => GroupModelSourceDisposition.NoWork,
                    _ => throw Unavailable()
                };
                if ((parsed == GroupModelSourceDisposition.Work) != used.Contains(source.MessageId)) throw Unavailable();
                dispositions.Add(new(source.MessageId, source.Revision, parsed));
            }
            if (!seen.SetEquals(candidates.Keys)) throw Unavailable();
            return new(preparation, notes.ToArray(), dispositions.ToArray());
        }
        catch (JsonException) { throw Unavailable(); }
        catch (ArgumentException) { throw Unavailable(); }
        catch (InvalidOperationException) { throw Unavailable(); }
        catch (KeyNotFoundException) { throw Unavailable(); }
    }

    private static GroupPreparedModelSource Source(JsonElement reference, Dictionary<Guid, GroupPreparedModelSource> candidates)
    {
        var text = Text(reference, "message_id", 36);
        if (!Guid.TryParseExact(text, "D", out var id) || id.ToString("D") != text || !candidates.TryGetValue(id, out var source)
            || reference.GetProperty("revision").ValueKind != JsonValueKind.Number
            || !reference.GetProperty("revision").TryGetInt64(out var revision) || revision != source.Revision) throw Unavailable();
        return source;
    }
    private static void Shape(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Unavailable();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name)) throw Unavailable();
        if (!names.SetEquals(expected)) throw Unavailable();
    }
    private static IEnumerable<JsonElement> Items(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw Unavailable();
        return value.EnumerateArray();
    }
    private static string Text(JsonElement value, string field, int maximum) => String(value.GetProperty(field), maximum);
    private static string String(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String) throw Unavailable();
        var text = value.GetString()!;
        if (text.Length > maximum || !text.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune)
            && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Format or UnicodeCategory.Control))) throw Unavailable();
        return text;
    }
    private static string? NullableText(JsonElement value, string field, int maximum) =>
        value.GetProperty(field).ValueKind == JsonValueKind.Null ? null : Text(value, field, maximum);
    private static InvalidOperationException Unavailable() => new("Group work proposal is not available.");
}
