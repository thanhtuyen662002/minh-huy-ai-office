using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Decoding establishes a closed private format, not original-source truth,
// current authorization, token qualification or provider/effect authority.
public sealed class GroupBrainAiNotePayload
{
    internal GroupBrainAiNotePayload(GroupWorkProposalKind kind, string title, string problem, string outcome,
        string[] missing, string? deadline, string? relation, GroupProposalEvidence[] evidence)
    {
        Kind = kind; TitleInterpretation = title; ProblemInterpretation = problem; RequestedOutcomeInterpretation = outcome;
        MissingFields = Array.AsReadOnly(missing); CustomerRequestedDeadlineText = deadline;
        SuggestedRelationHint = relation; Evidence = Array.AsReadOnly(evidence);
    }
    public GroupWorkProposalKind Kind { get; }
    public string TitleInterpretation { get; }
    public string ProblemInterpretation { get; }
    public string RequestedOutcomeInterpretation { get; }
    public IReadOnlyList<string> MissingFields { get; }
    public string? CustomerRequestedDeadlineText { get; }
    public string? SuggestedRelationHint { get; }
    public IReadOnlyList<GroupProposalEvidence> Evidence { get; }
    public override string ToString() => "Group brain AI note payload (private interpretation).";
}

public sealed class GroupBrainGlossaryPayload
{
    internal GroupBrainGlossaryPayload(string term, string explanation) { Term = term; Explanation = explanation; }
    public string Term { get; }
    public string Explanation { get; }
    public override string ToString() => "Group brain glossary payload (private content).";
}

public static class GroupBrainPayloadCodec
{
    public const string AiNoteVersion = "group-brain-ai-note-v1";
    public const string GlossaryVersion = "group-brain-glossary-v1";
    public const int MaximumPayloadUtf8Bytes = GroupBrainContentProtector.MaximumClearUtf8Bytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // This serialization goes only into protected private storage/context,
    // never HTML. Retain Unicode without inflating it to six-byte escapes.
    private static readonly JsonSerializerOptions Serialization = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string EncodeAiNote(GroupGroundedWorkNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (!Enum.IsDefined(note.Kind) || note.Evidence.Count is < 1 or > 10 || note.MissingFields.Count > 10) throw Unavailable();
        Plain(note.TitleInterpretation, 200); Plain(note.ProblemInterpretation, 2000); Plain(note.RequestedOutcomeInterpretation, 1000);
        foreach (var value in note.MissingFields) Plain(value, 200);
        if (note.CustomerRequestedDeadlineText is { } deadline) Plain(deadline, 1000);
        if (note.SuggestedRelationHint is { } relation) Plain(relation, 200);
        foreach (var reference in note.Evidence) Plain(reference.Quote, 4000);
        var result = JsonSerializer.Serialize(new
        {
            version = AiNoteVersion,
            interpretation_role = "source_backed_ai_interpretation",
            note_kind = KindText(note.Kind),
            title_interpretation = note.TitleInterpretation,
            problem_interpretation = note.ProblemInterpretation,
            requested_outcome_interpretation = note.RequestedOutcomeInterpretation,
            missing_fields = note.MissingFields,
            customer_requested_deadline_text = note.CustomerRequestedDeadlineText,
            suggested_relation_hint = note.SuggestedRelationHint,
            evidence = note.Evidence.Select(x => new { message_id = x.MessageId.ToString("D"), revision = x.Revision, quote = x.Quote })
        }, Serialization);
        _ = DecodeAiNote(result); return result;
    }

    // Only the glossary publisher can establish scope/Editor/publication
    // authority. This primitive merely validates its private payload format.
    public static string EncodeGlossary(string term, string explanation)
    {
        Plain(term, 200); Plain(explanation, 4000);
        var result = JsonSerializer.Serialize(new { version = GlossaryVersion, term, explanation }, Serialization);
        _ = DecodeGlossary(result); return result;
    }

    public static GroupBrainAiNotePayload DecodeAiNote(string? payload) => Decode(payload, root =>
    {
        Shape(root, "version", "interpretation_role", "note_kind", "title_interpretation", "problem_interpretation",
            "requested_outcome_interpretation", "missing_fields", "customer_requested_deadline_text", "suggested_relation_hint", "evidence");
        if (Text(root, "version", 80) != AiNoteVersion || Text(root, "interpretation_role", 80) != "source_backed_ai_interpretation") throw Unavailable();
        var kind = Text(root, "note_kind", 40) switch
        {
            "incident" => GroupWorkProposalKind.Incident,
            "change_request" => GroupWorkProposalKind.ChangeRequest,
            "request" => GroupWorkProposalKind.Request,
            "needs_clarification" => GroupWorkProposalKind.NeedsClarification,
            _ => throw Unavailable()
        };
        var title = Text(root, "title_interpretation", 200); var problem = Text(root, "problem_interpretation", 2000);
        var outcome = Text(root, "requested_outcome_interpretation", 1000);
        var missing = Items(root.GetProperty("missing_fields"), 10).Select(x => Plain(x, 200)).ToArray();
        if (missing.Distinct(StringComparer.Ordinal).Count() != missing.Length || kind == GroupWorkProposalKind.NeedsClarification && missing.Length == 0) throw Unavailable();
        var deadline = NullableText(root, "customer_requested_deadline_text", 1000); var relation = NullableText(root, "suggested_relation_hint", 200);
        var evidence = new List<GroupProposalEvidence>(); var unique = new HashSet<(Guid, long, string)>();
        foreach (var item in Items(root.GetProperty("evidence"), 10))
        {
            Shape(item, "message_id", "revision", "quote");
            var idText = Text(item, "message_id", 36);
            if (!Guid.TryParseExact(idText, "D", out var id) || id == Guid.Empty || id.ToString("D") != idText
                || !item.GetProperty("revision").TryGetInt64(out var revision) || revision <= 0) throw Unavailable();
            var quote = Text(item, "quote", 4000);
            if (!unique.Add((id, revision, quote))) throw Unavailable();
            evidence.Add(new(id, revision, quote));
        }
        if (evidence.Count == 0 || deadline is not null && !evidence.Any(x => x.Quote.Contains(deadline, StringComparison.Ordinal))) throw Unavailable();
        return new GroupBrainAiNotePayload(kind, title, problem, outcome, missing, deadline, relation, evidence.ToArray());
    });

    public static GroupBrainGlossaryPayload DecodeGlossary(string? payload) => Decode(payload, root =>
    {
        Shape(root, "version", "term", "explanation");
        if (Text(root, "version", 80) != GlossaryVersion) throw Unavailable();
        return new GroupBrainGlossaryPayload(Text(root, "term", 200), Text(root, "explanation", 4000));
    });

    private static T Decode<T>(string? payload, Func<JsonElement, T> parse)
    {
        try
        {
            if (payload is null || payload.Length > MaximumPayloadUtf8Bytes || StrictUtf8.GetByteCount(payload) > MaximumPayloadUtf8Bytes
                || GroupSecretQuarantine.InspectOutputJson(payload).RequiresQuarantine) throw Unavailable();
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            return parse(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw Unavailable(); }
    }
    private static string KindText(GroupWorkProposalKind kind) => kind switch
    {
        GroupWorkProposalKind.Incident => "incident",
        GroupWorkProposalKind.ChangeRequest => "change_request",
        GroupWorkProposalKind.Request => "request",
        GroupWorkProposalKind.NeedsClarification => "needs_clarification",
        _ => throw Unavailable()
    };
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
    private static string Text(JsonElement value, string field, int maximum) => Plain(value.GetProperty(field), maximum);
    private static string? NullableText(JsonElement value, string field, int maximum) =>
        value.GetProperty(field).ValueKind == JsonValueKind.Null ? null : Text(value, field, maximum);
    private static string Plain(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.String ? Plain(value.GetString()!, maximum) : throw Unavailable();
    private static string Plain(string text, int maximum)
    {
        if (text is null || text.Length > maximum || !text.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune)
            && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Format or UnicodeCategory.Control))) throw Unavailable();
        try { _ = StrictUtf8.GetByteCount(text); }
        catch (ArgumentException) { throw Unavailable(); }
        return text;
    }
    private static InvalidOperationException Unavailable() => new("Group brain payload is unavailable.");
}
