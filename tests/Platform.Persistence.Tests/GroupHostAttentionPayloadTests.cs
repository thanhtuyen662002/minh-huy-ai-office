using System.Collections;
using System.Text.Json.Nodes;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupHostAttentionPayloadTests
{
    [Theory]
    [InlineData(GroupHostAttentionReason.UnsupportedMedia, false)]
    [InlineData(GroupHostAttentionReason.UnsupportedMedia, true)]
    [InlineData(GroupHostAttentionReason.SecretQuarantine, false)]
    [InlineData(GroupHostAttentionReason.SecretQuarantine, true)]
    [InlineData(GroupHostAttentionReason.ExtractionFailed, false)]
    [InlineData(GroupHostAttentionReason.ExtractionFailed, true)]
    public void HostPayloadHasOnlyClosedMetadataAndNeverImpliesAiOrItTruth(GroupHostAttentionReason reason, bool gap)
    {
        var references = Enumerable.Range(1, 10).Select(x => new GroupHostAttentionReference(Guid.NewGuid(), x)).ToArray();
        var original = references.ToArray();
        var encoded = GroupBrainPayloadCodec.EncodeHostAttention(reason, gap, references);
        var decoded = GroupBrainPayloadCodec.DecodeHostAttention(encoded);
        Assert.Equal(reason, decoded.Reason); Assert.Equal(gap, decoded.HasCoverageGap);
        Assert.Equal(original, decoded.SourceReferences);
        references[0] = new(Guid.NewGuid(), 20); Assert.Equal(original, decoded.SourceReferences);
        Assert.Throws<NotSupportedException>(() => ((IList<GroupHostAttentionReference>)decoded.SourceReferences)[0] = references[0]);
        var shape = JsonNode.Parse(encoded)!.AsObject();
        Assert.Equal("host_metadata_attention", shape["interpretation_role"]!.GetValue<string>());
        Assert.Equal(6, shape.Count); Assert.Equal(2, shape["source_refs"]![0]!.AsObject().Count);
        Assert.Equal(reason == GroupHostAttentionReason.SecretQuarantine ? GroupSecretQuarantine.PolicyVersion : null, decoded.QuarantinePolicyVersion);
        Assert.DoesNotContain(original[0].MessageId.ToString("D"), decoded.ToString());
        foreach (var forbidden in new[] { "quote", "title", "problem", "customer_requested_deadline", "business_status", "assignee", "tenant_id", "destination", "verification_level" })
            Assert.False(shape.ContainsKey(forbidden));
        Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.DecodeAiNote(encoded));
        Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.DecodeGlossary(encoded));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("reason")]
    [InlineData("version")]
    [InlineData("gap_string")]
    [InlineData("gap_null")]
    [InlineData("extra_secret")]
    [InlineData("extra_status")]
    [InlineData("extra_quote")]
    [InlineData("missing_gap")]
    [InlineData("empty_refs")]
    [InlineData("overflow_refs")]
    [InlineData("duplicate_refs")]
    [InlineData("duplicate_message_new_revision")]
    [InlineData("uppercase_id")]
    [InlineData("zero_id")]
    [InlineData("zero_revision")]
    [InlineData("string_revision")]
    [InlineData("fraction_revision")]
    [InlineData("overflow_revision")]
    [InlineData("policy_for_media")]
    [InlineData("missing_quarantine_policy")]
    [InlineData("wrong_quarantine_policy")]
    public void HostPayloadRefusesMalformedOrForgedAuthorityWithFixedPrivateError(string fault)
    {
        var wire = JsonNode.Parse(GroupBrainPayloadCodec.EncodeHostAttention(GroupHostAttentionReason.UnsupportedMedia, false,
            [new(Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab"), 1)]))!.AsObject();
        var refs = wire["source_refs"]!.AsArray(); var reference = refs[0]!.AsObject();
        switch (fault)
        {
            case "role": wire["interpretation_role"] = "it_confirmed"; break;
            case "reason": wire["reason"] = "customer_issue_resolved"; break;
            case "version": wire["version"] = "unknown-v2"; break;
            case "gap_string": wire["has_coverage_gap"] = "false"; break;
            case "gap_null": wire["has_coverage_gap"] = null; break;
            case "extra_secret": wire["private"] = "password=NEVER_EXPORT"; break;
            case "extra_status": wire["business_status"] = "resolved"; break;
            case "extra_quote": reference["quote"] = "NEVER_EXPORT"; break;
            case "missing_gap": wire.Remove("has_coverage_gap"); break;
            case "empty_refs": refs.Clear(); break;
            case "overflow_refs": for (var i = 0; i < 10; i++) refs.Add(new JsonObject { ["message_id"] = Guid.NewGuid().ToString("D"), ["revision"] = 1 }); break;
            case "duplicate_refs": refs.Add(reference.DeepClone()); break;
            case "duplicate_message_new_revision": var duplicate = reference.DeepClone(); duplicate["revision"] = 2; refs.Add(duplicate); break;
            case "uppercase_id": reference["message_id"] = reference["message_id"]!.GetValue<string>().ToUpperInvariant(); break;
            case "zero_id": reference["message_id"] = Guid.Empty.ToString("D"); break;
            case "zero_revision": reference["revision"] = 0; break;
            case "string_revision": reference["revision"] = "1"; break;
            case "fraction_revision": reference["revision"] = 1.5; break;
            case "overflow_revision": reference["revision"] = JsonNode.Parse("9223372036854775808"); break;
            case "policy_for_media": wire["quarantine_policy_version"] = GroupSecretQuarantine.PolicyVersion; break;
            case "missing_quarantine_policy": wire["reason"] = "secret_quarantine"; break;
            case "wrong_quarantine_policy": wire["reason"] = "secret_quarantine"; wire["quarantine_policy_version"] = "unknown-policy"; break;
            default: throw new InvalidOperationException();
        }
        Refused(wire.ToJsonString());
    }

    [Fact]
    public void HostPayloadBoundsRealEnumerationAndDecodedNamesUtf8AndPrivateDiagnostics()
    {
        var references = Enumerable.Range(1, 11).Select(x => new GroupHostAttentionReference(Guid.NewGuid(), x)).ToArray();
        Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.EncodeHostAttention(GroupHostAttentionReason.UnsupportedMedia, false, new LyingReferences(references)));
        foreach (var invalid in new IReadOnlyList<GroupHostAttentionReference>?[] { null, [], [null!], [new(Guid.Empty, 1)], [new(Guid.NewGuid(), 0)] })
            Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.EncodeHostAttention(GroupHostAttentionReason.UnsupportedMedia, false, invalid));
        Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.EncodeHostAttention((GroupHostAttentionReason)99, false, [references[0]]));
        var valid = GroupBrainPayloadCodec.EncodeHostAttention(GroupHostAttentionReason.ExtractionFailed, false, [references[0]]);
        foreach (var invalid in new[] { valid.Replace("\"version\":", "\"version\":\"group-brain-host-attention-v1\",\"vers\\u0069on\":"),
            valid.Replace("\"revision\":", "\"revision\":1,\"rev\\u0069sion\":"), "[]", "null", "{\"private\":\"\uD800\"}", valid + new string(' ', 64001) })
            Refused(invalid);
    }
    private static void Refused(string? wire)
    {
        var error = Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.DecodeHostAttention(wire));
        Assert.Equal("Group brain payload is unavailable.", error.Message); Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(11)]
    public void HostEncoderUsesActualEnumerationEvenWhenIListClaimsOneItem(int actualCount)
    {
        var references = Enumerable.Range(1, actualCount).Select(x => new GroupHostAttentionReference(Guid.NewGuid(), x)).ToArray();
        var caller = new LyingReferences(references);
        Assert.True(caller.Count == 1); // Deliberately false advertised size, not enumeration size.
        if (actualCount is 0 or 11)
        {
            var error = Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.EncodeHostAttention(GroupHostAttentionReason.UnsupportedMedia, false, caller));
            Assert.Equal("Group brain payload is unavailable.", error.Message); Assert.Null(error.InnerException);
        }
        else
            Assert.Equal(references, GroupBrainPayloadCodec.DecodeHostAttention(GroupBrainPayloadCodec.EncodeHostAttention(GroupHostAttentionReason.UnsupportedMedia, false, caller)).SourceReferences);
    }
    [Fact]
    public void HostEncoderStopsAfterEleventhActualItemAndDisposesEnumerator()
    {
        var caller = new UnboundedReferences();
        Assert.Throws<InvalidOperationException>(() => GroupBrainPayloadCodec.EncodeHostAttention(GroupHostAttentionReason.UnsupportedMedia, false, caller));
        Assert.Equal(11, caller.Visited); Assert.True(caller.Disposed);
    }
    private sealed class LyingReferences(GroupHostAttentionReference[] values) : IReadOnlyList<GroupHostAttentionReference>, IList<GroupHostAttentionReference>
    {
        public int Count => 1;
        public GroupHostAttentionReference this[int index] { get => values[index]; set => throw new NotSupportedException(); }
        public bool IsReadOnly => true;
        public void Add(GroupHostAttentionReference item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(GroupHostAttentionReference item) => throw new NotSupportedException();
        public void CopyTo(GroupHostAttentionReference[] array, int arrayIndex) => throw new NotSupportedException();
        public int IndexOf(GroupHostAttentionReference item) => throw new NotSupportedException();
        public void Insert(int index, GroupHostAttentionReference item) => throw new NotSupportedException();
        public bool Remove(GroupHostAttentionReference item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public IEnumerator<GroupHostAttentionReference> GetEnumerator() => ((IEnumerable<GroupHostAttentionReference>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class UnboundedReferences : IReadOnlyList<GroupHostAttentionReference>
    {
        public int Count => throw new InvalidOperationException("Count must not be read.");
        public GroupHostAttentionReference this[int index] => throw new InvalidOperationException("Indexer must not be read.");
        public int Visited { get; private set; }
        public bool Disposed { get; private set; }
        public IEnumerator<GroupHostAttentionReference> GetEnumerator()
        {
            try
            {
                while (true)
                {
                    if (++Visited > 11) throw new InvalidOperationException("Enumeration exceeded the sentinel.");
                    yield return new(Guid.NewGuid(), Visited);
                }
            }
            finally { Disposed = true; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
