using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static async Task<GroupGroundedWorkNote> PayloadNoteAsync(Fixture f, string type = "request")
    {
        var message = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho ngày mai 😀\uFEFF "));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var wire = ProposalWire(Assert.Single(preparation.Candidates)); var note = wire["notes"]![0]!;
        note["type"] = type; note["requested_deadline_text"] = "ngày mai"; note["suggested_relation"] = "Có thể liên quan REQ-X, chưa xác nhận";
        if (type == "needs_clarification") note["missing_fields"] = new JsonArray("Mã kho");
        return Assert.Single(GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()).Notes);
    }

    [Theory]
    [InlineData("incident")]
    [InlineData("change_request")]
    [InlineData("request")]
    [InlineData("needs_clarification")]
    public async Task BrainAiPayloadRoundTripRetainsInterpretationsAndExactEvidenceWithoutEffects(string type)
    {
        using var f = new Fixture(); var note = await PayloadNoteAsync(f, type);
        var before = await f.CountsAsync(); var keyReads = f.Keys.Reads;
        var payload = GroupBrainPayloadCodec.EncodeAiNote(note); var decoded = GroupBrainPayloadCodec.DecodeAiNote(payload);
        Assert.Equal(note.Kind, decoded.Kind); Assert.Equal(note.TitleInterpretation, decoded.TitleInterpretation);
        Assert.Equal(note.ProblemInterpretation, decoded.ProblemInterpretation); Assert.Equal(note.RequestedOutcomeInterpretation, decoded.RequestedOutcomeInterpretation);
        Assert.Equal(note.MissingFields, decoded.MissingFields); Assert.Equal(note.CustomerRequestedDeadlineText, decoded.CustomerRequestedDeadlineText);
        Assert.Equal(note.SuggestedRelationHint, decoded.SuggestedRelationHint);
        var original = Assert.Single(note.Evidence); var evidence = Assert.Single(decoded.Evidence);
        Assert.Equal(original.MessageId, evidence.MessageId); Assert.Equal(original.Revision, evidence.Revision); Assert.Equal(original.Quote, evidence.Quote);
        Assert.DoesNotContain(original.Quote, decoded.ToString()); Assert.DoesNotContain(original.Quote, evidence.ToString());
        var shape = JsonNode.Parse(payload)!.AsObject(); Assert.Equal("source_backed_ai_interpretation", shape["interpretation_role"]!.GetValue<string>());
        foreach (var name in new[] { "business_status", "committed_due_at", "assignee", "tenant_id", "destination", "verification_level" }) Assert.False(shape.ContainsKey(name));
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keyReads, f.Keys.Reads);
    }

    [Theory]
    [InlineData("extra_status")]
    [InlineData("extra_scope")]
    [InlineData("extra_destination")]
    [InlineData("role")]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("missing_field")]
    [InlineData("empty_title")]
    [InlineData("format_only_title")]
    [InlineData("long_problem")]
    [InlineData("empty_evidence")]
    [InlineData("over_evidence")]
    [InlineData("duplicate_evidence")]
    [InlineData("noncanonical_id")]
    [InlineData("zero_id")]
    [InlineData("zero_revision")]
    [InlineData("fraction_revision")]
    [InlineData("string_revision")]
    [InlineData("evidence_extra_field")]
    [InlineData("wrong_deadline")]
    [InlineData("missing_clarification")]
    [InlineData("duplicate_missing")]
    [InlineData("over_missing")]
    [InlineData("secret_title")]
    [InlineData("secret_evidence")]
    public async Task BrainAiPayloadClosedDecodedShapeAndKnownSecretPolicyFailWithoutPrivateDiagnostics(string fault)
    {
        using var f = new Fixture(); var note = await PayloadNoteAsync(f);
        var wire = JsonNode.Parse(GroupBrainPayloadCodec.EncodeAiNote(note))!.AsObject();
        var evidence = wire["evidence"]!.AsArray(); var reference = evidence[0]!.AsObject();
        switch (fault)
        {
            case "extra_status": wire["business_status"] = "resolved"; break;
            case "extra_scope": wire["tenant_id"] = "PRIVATE"; break;
            case "extra_destination": wire["destination"] = "PRIVATE"; break;
            case "role": wire["interpretation_role"] = "it_confirmed"; break;
            case "version": wire["version"] = "unknown-v2"; break;
            case "kind": wire["note_kind"] = "resolved"; break;
            case "missing_field": wire.Remove("suggested_relation_hint"); break;
            case "empty_title": wire["title_interpretation"] = " "; break;
            case "format_only_title": wire["title_interpretation"] = "\uFEFF\u200B"; break;
            case "long_problem": wire["problem_interpretation"] = new string('X', 2001); break;
            case "empty_evidence": evidence.Clear(); break;
            case "over_evidence": for (var i = 0; i < 10; i++) evidence.Add(reference.DeepClone()); break;
            case "duplicate_evidence": evidence.Add(reference.DeepClone()); break;
            case "noncanonical_id": reference["message_id"] = note.Evidence[0].MessageId.ToString("N"); break;
            case "zero_id": reference["message_id"] = Guid.Empty.ToString("D"); break;
            case "zero_revision": reference["revision"] = 0; break;
            case "fraction_revision": reference["revision"] = 1.5; break;
            case "string_revision": reference["revision"] = "1"; break;
            case "evidence_extra_field": reference["is_confirmed"] = true; break;
            case "wrong_deadline": wire["customer_requested_deadline_text"] = "PRIVATE missing deadline"; break;
            case "missing_clarification": wire["note_kind"] = "needs_clarification"; break;
            case "duplicate_missing": wire["missing_fields"] = new JsonArray("Mã kho", "Mã kho"); break;
            case "over_missing": wire["missing_fields"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 11).Select(i => "Mã kho " + i)); break;
            case "secret_title": wire["title_interpretation"] = "password=PRIVATE"; break;
            case "secret_evidence": reference["quote"] = "Authorization: Bearer PRIVATECREDENTIAL123"; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.DecodeAiNote(wire.ToJsonString()));
        Assert.Equal("Group brain payload is unavailable.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task BrainPayloadRejectsDuplicateDecodedFieldsMalformedUtf16AndUtf8ByteOverflow()
    {
        using var f = new Fixture(); var note = await PayloadNoteAsync(f); var payload = GroupBrainPayloadCodec.EncodeAiNote(note);
        var duplicate = payload.Replace("\"version\":", "\"version\":\"group-brain-ai-note-v1\",\"vers\\u0069on\":", StringComparison.Ordinal);
        var overflow = JsonNode.Parse(payload)!.AsObject(); var evidence = overflow["evidence"]!.AsArray(); evidence.Clear();
        for (var i = 0; i < 6; i++) evidence.Add(new JsonObject { ["message_id"] = Guid.NewGuid().ToString("D"), ["revision"] = 1, ["quote"] = new string('漢', 4000) });
        var raw = overflow.ToJsonString(new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.True(raw.Length < 64000); Assert.True(Encoding.UTF8.GetByteCount(raw) > 64000);
        foreach (var value in new[] { duplicate, raw, "{\"version\":\"\uD800\"}", "[]", "null", payload + new string(' ', 64001) })
        {
            var error = Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.DecodeAiNote(value));
            Assert.Equal("Group brain payload is unavailable.", error.Message); Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void BrainGlossaryPreservesUnicodeAndWhitespaceAsPrivateUnqualifiedContent()
    {
        const string term = " Kho 😀\uFEFF "; const string meaning = " Tồn kho và nhập xuất. \n";
        var encoded = GroupBrainPayloadCodec.EncodeGlossary(term, meaning); var decoded = GroupBrainPayloadCodec.DecodeGlossary(encoded);
        Assert.Equal(term, decoded.Term); Assert.Equal(meaning, decoded.Explanation); Assert.DoesNotContain(term, decoded.ToString());
        Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.DecodeAiNote(encoded));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("version")]
    [InlineData("empty")]
    [InlineData("long")]
    [InlineData("secret")]
    [InlineData("type")]
    [InlineData("missing")]
    public void BrainGlossaryClosedShapeAndQuarantineDoNotPublishEditorAuthority(string fault)
    {
        var wire = JsonNode.Parse(GroupBrainPayloadCodec.EncodeGlossary("Kho", "Kho hàng"))!.AsObject();
        switch (fault)
        {
            case "extra": wire["allow_extraction"] = true; break;
            case "version": wire["version"] = "unknown"; break;
            case "empty": wire["term"] = "\u200B\uFEFF "; break;
            case "long": wire["explanation"] = new string('X', 4001); break;
            case "secret": wire["explanation"] = "apiKey=PRIVATE"; break;
            case "type": wire["term"] = 7; break;
            case "missing": wire.Remove("explanation"); break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.DecodeGlossary(wire.ToJsonString()));
        Assert.Equal("Group brain payload is unavailable.", error.Message); Assert.Null(error.InnerException);
    }
}
