extern alias RuntimeWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static JsonObject ProposalWire(GroupPreparedModelSource source) => JsonSerializer.SerializeToNode(new
    {
        version = GroupGroundedWorkProposal.FormatVersion,
        notes = new[] { new { type = "request", title = "Tra cứu tồn kho", problem = "Khách yêu cầu tra cứu",
            outcome = "IT xem tồn kho", source_refs = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, quote = source.Text } },
            missing_fields = Array.Empty<string>(), requested_deadline_text = (string?)null, suggested_relation = (string?)null } },
        source_dispositions = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, disposition = "work" } }
    })!.AsObject();

    [Fact]
    public async Task GroundedProposalRetainsExactOriginalQuotesAndCustomerDeadlineWithoutSqlEffects()
    {
        using var f = new Fixture();
        const string original = "Tra cứu tồn kho ngày mai 😀\uFEFF ";
        var message = await f.CommitAsync(f.Payload(text: original));
        var context = await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]);
        var preparation = GroupBatchSourcePreparation.Create(context);
        var wire = ProposalWire(Assert.Single(preparation.Candidates));
        wire["notes"]![0]!["requested_deadline_text"] = "ngày mai";
        wire["notes"]![0]!["suggested_relation"] = "Có thể liên quan MH-123, chưa xác nhận";
        var before = await f.CountsAsync();
        var proposal = GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString());
        Assert.Equal(preparation.Scope, proposal.Scope); Assert.Equal(preparation.BatchId, proposal.BatchId);
        var note = Assert.Single(proposal.Notes); var evidence = Assert.Single(note.Evidence);
        Assert.Equal(original, evidence.Quote); Assert.Equal(message.MessageId, evidence.MessageId); Assert.Equal(message.Revision, evidence.Revision);
        Assert.Equal("ngày mai", note.CustomerRequestedDeadlineText); Assert.Same(evidence, note.DeadlineEvidence);
        Assert.Contains("chưa xác nhận", note.SuggestedRelationHint);
        Assert.Equal(GroupModelSourceDisposition.Work, Assert.Single(proposal.SourceDispositions).Disposition);
        Assert.Equal(before, await f.CountsAsync());
        Assert.DoesNotContain(original, proposal.ToString()); Assert.DoesNotContain(original, note.ToString()); Assert.DoesNotContain(original, evidence.ToString());
    }

    [Theory]
    [InlineData("foreign_id")]
    [InlineData("noncanonical_id")]
    [InlineData("revision")]
    [InlineData("fraction_revision")]
    [InlineData("string_revision")]
    [InlineData("wrong_quote")]
    [InlineData("normalized_quote")]
    [InlineData("empty_evidence")]
    [InlineData("duplicate_evidence")]
    [InlineData("deadline_outside_quote")]
    [InlineData("missing_coverage")]
    [InlineData("duplicate_coverage")]
    [InlineData("no_work_with_evidence")]
    [InlineData("work_without_note")]
    [InlineData("invented_it_status")]
    [InlineData("invented_tenant")]
    [InlineData("invented_due_date")]
    [InlineData("invented_destination")]
    [InlineData("unsupported_type")]
    [InlineData("unsupported_disposition")]
    [InlineData("missing_field")]
    [InlineData("duplicate_note")]
    [InlineData("extra_evidence_field")]
    [InlineData("too_many_notes")]
    [InlineData("too_many_missing_fields")]
    [InlineData("duplicate_missing_field")]
    [InlineData("secret_assignment")]
    [InlineData("format_only_quote")]
    [InlineData("format_only_title")]
    [InlineData("format_only_missing_field")]
    public async Task UntrustedProposalsFailClosedWithoutPrivateErrorsOrSqlEffects(string fault)
    {
        using var f = new Fixture(); const string original = "Tra cứu tồn kho 😀\uFEFF ";
        var message = await f.CommitAsync(f.Payload(text: original));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var source = Assert.Single(preparation.Candidates); var wire = ProposalWire(source);
        var notes = wire["notes"]!.AsArray(); var note = notes[0]!.AsObject();
        var references = note["source_refs"]!.AsArray(); var reference = references[0]!.AsObject();
        var dispositions = wire["source_dispositions"]!.AsArray();
        switch (fault)
        {
            case "foreign_id": reference["message_id"] = Guid.NewGuid().ToString("D"); break;
            case "noncanonical_id": reference["message_id"] = source.MessageId.ToString("B"); break;
            case "revision": reference["revision"] = source.Revision + 1; break;
            case "fraction_revision": reference["revision"] = 1.5; break;
            case "string_revision": reference["revision"] = source.Revision.ToString(); break;
            case "wrong_quote": reference["quote"] = "PRIVATE_SENTINEL_72691"; break;
            case "normalized_quote": reference["quote"] = original.Replace("\uFEFF", "", StringComparison.Ordinal); break;
            case "empty_evidence": references.Clear(); break;
            case "duplicate_evidence": references.Add(reference.DeepClone()); break;
            case "deadline_outside_quote": note["requested_deadline_text"] = "PRIVATE_SENTINEL_72691"; break;
            case "missing_coverage": dispositions.Clear(); break;
            case "duplicate_coverage": dispositions.Add(dispositions[0]!.DeepClone()); break;
            case "no_work_with_evidence": dispositions[0]!["disposition"] = "no_work"; break;
            case "work_without_note": notes.Clear(); break;
            case "invented_it_status": note["status"] = "resolved"; break;
            case "invented_tenant": wire["tenant_id"] = Guid.NewGuid().ToString("D"); break;
            case "invented_due_date": note["committed_due_at"] = "2026-10-11"; break;
            case "invented_destination": note["destination"] = "customer-group"; break;
            case "unsupported_type": note["type"] = "resolved"; break;
            case "unsupported_disposition": dispositions[0]!["disposition"] = "ignore"; break;
            case "missing_field": note.Remove("problem"); break;
            case "duplicate_note": notes.Add(note.DeepClone()); break;
            case "extra_evidence_field": reference["binding_id"] = Guid.NewGuid().ToString("D"); break;
            case "too_many_notes": for (var i = 0; i < 20; i++) notes.Add(note.DeepClone()); break;
            case "too_many_missing_fields": note["missing_fields"] = new JsonArray(Enumerable.Range(0, 11).Select(i => JsonValue.Create("field-" + i)).ToArray()); break;
            case "duplicate_missing_field": note["missing_fields"] = new JsonArray("module", "module"); break;
            case "secret_assignment": note["problem"] = "password=PRIVATE_SENTINEL_72691"; break;
            case "format_only_quote": reference["quote"] = "\uFEFF "; break;
            case "format_only_title": note["title"] = "\uFEFF \u200B"; break;
            case "format_only_missing_field": note["missing_fields"] = new JsonArray("\uFEFF \u200B"); break;
            default: throw new InvalidOperationException("Unknown test fault.");
        }
        var before = await f.CountsAsync();
        var failure = Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
        Assert.Equal("Group work proposal is not available.", failure.Message); Assert.Null(failure.InnerException);
        Assert.Equal(before, await f.CountsAsync());
    }

    [Fact]
    public async Task EachModelEligibleSourceNeedsDispositionAndHostQuarantineCannotBeReintroduced()
    {
        using var f = new Fixture();
        var normal = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho"));
        var social = await f.CommitAsync(f.Payload(messageId: "social", eventId: "social-event", text: "Cảm ơn"));
        var secret = await f.CommitAsync(f.Payload(messageId: "secret", eventId: "secret-event", text: "password=PRIVATE_SENTINEL_72691"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [normal.MessageId, social.MessageId, secret.MessageId]));
        var wire = ProposalWire(preparation.Candidates.Single(x => x.MessageId == normal.MessageId));
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
        wire["source_dispositions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { message_id = social.MessageId.ToString("D"), revision = social.Revision, disposition = "no_work" }));
        var parsed = GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString());
        Assert.Equal(2, parsed.SourceDispositions.Count); Assert.Single(parsed.Notes);
        Assert.Equal(GroupSourcePreparationDisposition.Quarantined, preparation.Receipts.Single(x => x.MessageId == secret.MessageId).Disposition);
        wire["notes"]![0]!["source_refs"]![0]!["message_id"] = secret.MessageId.ToString("D");
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
    }

    [Fact]
    public async Task SeveralSourcesMaySupportOneNoteAndOneSourceMaySupportSeveralDistinctNotes()
    {
        using var f = new Fixture();
        var first = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho và nhập xuất"));
        var second = await f.CommitAsync(f.Payload(messageId: "second", eventId: "second-event", text: "Bổ sung kho HCM"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [first.MessageId, second.MessageId]));
        var wire = ProposalWire(preparation.Candidates.Single(source => source.MessageId == first.MessageId));
        var otherNote = wire["notes"]![0]!.DeepClone(); otherNote["title"] = "Tra cứu nhập xuất";
        wire["notes"]![0]!["source_refs"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { message_id = second.MessageId.ToString("D"), revision = second.Revision, quote = "kho HCM" }));
        wire["notes"]!.AsArray().Add(otherNote);
        wire["source_dispositions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { message_id = second.MessageId.ToString("D"), revision = second.Revision, disposition = "work" }));
        var proposal = GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString());
        Assert.Equal(2, proposal.Notes.Count); Assert.Equal(2, proposal.Notes[0].Evidence.Count); Assert.Single(proposal.Notes[1].Evidence);
        Assert.All(proposal.Notes, note => Assert.Contains(note.Evidence, evidence => evidence.MessageId == first.MessageId));
        Assert.Throws<NotSupportedException>(() => ((IList<GroupGroundedWorkNote>)proposal.Notes).Add(proposal.Notes[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<GroupProposalEvidence>)proposal.Notes[0].Evidence).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GroupProposalSourceReceipt>)proposal.SourceDispositions).Clear());
    }

    [Fact]
    public async Task TotalEvidenceAndPerNoteBoundsHaveAcceptedAdjacentControls()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: new string('a', 100)));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var wire = ProposalWire(Assert.Single(preparation.Candidates)); var template = wire["notes"]![0]!.DeepClone();
        var references = template["source_refs"]!.AsArray(); references.Clear();
        for (var i = 1; i <= 10; i++) references.Add(JsonSerializer.SerializeToNode(new { message_id = message.MessageId.ToString("D"), revision = message.Revision, quote = new string('a', i) }));
        wire["notes"]!.AsArray().Clear();
        for (var i = 0; i < 10; i++) { var note = template.DeepClone(); note["title"] = "owned distinct interpretation " + i; wire["notes"]!.AsArray().Add(note); }
        var proposal = GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString());
        Assert.Equal(100, proposal.Notes.Sum(note => note.Evidence.Count));
        var overflow = template.DeepClone(); overflow["title"] = "owned adjacent interpretation";
        wire["notes"]!.AsArray().Add(overflow);
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
        wire["notes"]!.AsArray().RemoveAt(10);
        wire["notes"]![0]!["source_refs"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { message_id = message.MessageId.ToString("D"), revision = message.Revision, quote = new string('a', 11) }));
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
    }

    [Fact]
    public async Task NoteCountAndUtf8OutputBoundsHaveAcceptedAdjacentControls()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "owned-synthetic"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var wire = ProposalWire(Assert.Single(preparation.Candidates)); var template = wire["notes"]![0]!.DeepClone();
        wire["notes"]!.AsArray().Clear();
        for (var i = 0; i < 20; i++) { var note = template.DeepClone(); note["title"] = "owned interpretation " + i; wire["notes"]!.AsArray().Add(note); }
        Assert.Equal(20, GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()).Notes.Count);
        var overflow = template.DeepClone(); overflow["title"] = "owned adjacent interpretation"; wire["notes"]!.AsArray().Add(overflow);
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
        wire["notes"]!.AsArray().RemoveAt(20);
        var output = wire.ToJsonString(); var remaining = GroupGroundedWorkProposal.MaximumOutputUtf8Bytes - Encoding.UTF8.GetByteCount(output);
        var bounded = output + new string(' ', remaining);
        Assert.Equal(20, GroupGroundedWorkProposal.Parse(preparation, bounded).Notes.Count);
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, bounded + " "));
    }

    [Fact]
    public async Task EveryOneOfOneHundredCandidateSourcesNeedsExactlyOneDisposition()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var i = 0; i < 100; i++) ids.Add((await f.CommitAsync(f.Payload(messageId: "message-" + i, eventId: "event-" + i, text: "owned synthetic social " + i))).MessageId);
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), ids));
        Assert.Equal(100, preparation.Candidates.Count);
        var wire = JsonSerializer.SerializeToNode(new
        {
            version = GroupGroundedWorkProposal.FormatVersion,
            notes = Array.Empty<object>(),
            source_dispositions = preparation.Candidates.Select(source => new { message_id = source.MessageId.ToString("D"), revision = source.Revision, disposition = "no_work" }).ToArray()
        })!.AsObject();
        Assert.Equal(100, GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()).SourceDispositions.Count);
        wire["source_dispositions"]!.AsArray().RemoveAt(99);
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
    }

    [Fact]
    public async Task NestedDecodedDuplicateNamesCannotChooseAnApparentlyValidLastEvidenceValue()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var output = ProposalWire(Assert.Single(preparation.Candidates)).ToJsonString();
        foreach (var duplicate in new[] { output.Replace("\"quote\":", "\"q\\u0075ote\":\"PRIVATE_SENTINEL_72691\",\"quote\":", StringComparison.Ordinal),
            output.Replace("\"problem\":", "\"pr\\u006fblem\":\"PRIVATE_SENTINEL_72691\",\"problem\":", StringComparison.Ordinal) })
        {
            var failure = Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, duplicate));
            Assert.Equal("Group work proposal is not available.", failure.Message); Assert.Null(failure.InnerException);
        }
    }

    [Fact]
    public async Task ParserRejectsDecodedDuplicatePropertiesAndPrivateMalformedOutputs()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var output = ProposalWire(Assert.Single(preparation.Candidates)).ToJsonString();
        var duplicate = output.Insert(1, "\"ver\\u0073ion\":\"group-work-proposal-v1\",");
        foreach (var value in new[] { duplicate, null, "{PRIVATE_SENTINEL_72691}", "{\"title\":\"\\uD800\"}", "[]",
            output.Replace("\"request\"", "\"" + new string('x', 64000) + "\"", StringComparison.Ordinal) })
        {
            var failure = Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, value));
            Assert.Equal("Group work proposal is not available.", failure.Message); Assert.Null(failure.InnerException);
        }
    }

    [Fact]
    public async Task NoWorkIsHonestProposalAndDoesNotAuthorizeEffectsAfterLaterRecall()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Cảm ơn"));
        var context = await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]);
        var preparation = GroupBatchSourcePreparation.Create(context); var wire = ProposalWire(Assert.Single(preparation.Candidates));
        wire["notes"]!.AsArray().Clear(); wire["source_dispositions"]![0]!["disposition"] = "no_work";
        var before = await f.CountsAsync(); var proposal = GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString());
        Assert.Empty(proposal.Notes); Assert.Equal(GroupModelSourceDisposition.NoWork, Assert.Single(proposal.SourceDispositions).Disposition);
        Assert.Equal(before, await f.CountsAsync());
        await f.CommitAsync(f.Payload(eventId: "later-recall", kind: GroupSourceEventKind.Recall, text: ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.RequireCurrentAsync(context));
        Assert.Empty(proposal.Notes); // Parsing never confers a later release/commit capability.
    }

    [Fact]
    public async Task ExistingStructuredGatewayAcceptsProposalSchemaOnActualAdapterPathWithSyntheticResponse()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var output = ProposalWire(Assert.Single(preparation.Candidates)).ToJsonString();
        using var handler = new ProposalHandler(output); using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, new(new Uri("https://owned.invalid/v1/responses"), "synthetic-model", "Bearer synthetic-fixture", "synthetic-provider"));
        var gateway = new OrderedFailoverAiGateway(adapter);
        var request = new AiGatewayRequest("owned-tenant", "owned-company", "owned-batch", "owned-operation", AiCapability.StructuredGeneration,
            "Owned synthetic schema compatibility control.", GroupGroundedWorkProposal.ResponseSchema);
        var response = await gateway.ExecuteAsync(request); response.ValidateFor(request);
        Assert.Equal(1, handler.Calls); Assert.Single(GroupGroundedWorkProposal.Parse(preparation, response.Output).Notes);
    }

    [Fact]
    public void GroundedProposalTypesCannotBeForgedMutatedOrCarryItAuthority()
    {
        foreach (var type in new[] { typeof(GroupGroundedWorkProposal), typeof(GroupGroundedWorkNote), typeof(GroupProposalEvidence), typeof(GroupProposalSourceReceipt) })
        {
            Assert.Empty(type.GetConstructors()); Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
            Assert.DoesNotContain(type.GetProperties(), property => property.Name is "Status" or "CommittedDueAt" or "Assignee" or "Destination" or "TenantId" or "CompanyId");
        }
        Assert.Throws<ArgumentNullException>(() => GroupGroundedWorkProposal.Parse(null!, "{}"));
    }

    private sealed class ProposalHandler(string output) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            var format = body.RootElement.GetProperty("text").GetProperty("format");
            Assert.True(format.GetProperty("strict").GetBoolean());
            using var schema = JsonDocument.Parse(GroupGroundedWorkProposal.ResponseSchema);
            Assert.True(JsonElement.DeepEquals(schema.RootElement, format.GetProperty("schema")));
            var envelope = JsonSerializer.Serialize(new
            {
                status = "completed",
                model = "synthetic-model",
                output = new[] { new { type = "message", status = "completed", role = "assistant", content = new[] { new { type = "output_text", text = output } } } },
                usage = new { input_tokens = 3, output_tokens = 2, total_tokens = 5 }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
        }
    }
}
