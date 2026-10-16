using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupStructuredAdmissionSnapshotCodecTests
{
    private static string Hash(char c = 'A') => new(c, 64);
    private static AiStructuredAdmissionSnapshot Snapshot(bool maximum = false)
    {
        var routes = Enumerable.Range(0, maximum ? 2 : 1).Select(index => new AiStructuredAdmissionRoute(index,
            maximum ? new string((char)('a' + index), 128) : "primary", maximum ? new('m', 256) : "configured",
            Hash('B'), Hash('C'), Hash('D'), 10000, 5, 8, 13,
            maximum ? Enumerable.Range(0, 16).Select(n => new string('e', 254) + n.ToString("D2")).ToArray() : ["a-echo", "z-echo"])).ToArray();
        return new(Hash(), TimeSpan.FromSeconds(60).Ticks, Hash('B'), 20, Hash('C'), 30,
            maximum ? new('t', 128) : "scalar", 5, 3, routes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canonical_roundtrip_is_bounded_and_retains_full_ordered_metadata(bool maximum)
    {
        var original = Snapshot(maximum); var encoded = GroupStructuredAdmissionSnapshotCodec.Write(original);
        Assert.InRange(encoded.Length, 256, GroupStructuredAdmissionSnapshotCodec.MaximumBytes);
        Assert.Equal("AIOGADM1", Encoding.ASCII.GetString(encoded.AsSpan(0, 8)));
        var decoded = GroupStructuredAdmissionSnapshotCodec.Read(encoded);
        Assert.Equal(original.SnapshotSha256, decoded.SnapshotSha256);
        Assert.Equal(encoded, GroupStructuredAdmissionSnapshotCodec.Write(decoded));
        encoded[0] = 0;
        Assert.Equal(original.SnapshotSha256, decoded.SnapshotSha256);
        Assert.False((object)decoded is IAiPreparedStructuredRequest);
    }

    [Fact]
    public void Every_truncation_and_changed_checksum_refuse_without_private_diagnostics()
    {
        var encoded = GroupStructuredAdmissionSnapshotCodec.Write(Snapshot());
        for (var length = 0; length < encoded.Length; length++) Denied(encoded[..length]);
        encoded[^1] ^= 1; Denied(encoded);
        Denied(new byte[GroupStructuredAdmissionSnapshotCodec.MaximumBytes + 1]);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("route-count")]
    [InlineData("input-count")]
    [InlineData("utf8")]
    [InlineData("length")]
    [InlineData("trailing")]
    [InlineData("alias-order")]
    public void A_recomputed_untrusted_checksum_cannot_admit_noncanonical_or_invalid_metadata(string change)
    {
        var encoded = GroupStructuredAdmissionSnapshotCodec.Write(Snapshot());
        var body = encoded[..^32];
        // Fixed fields end at offset120. A bounded two-byte tokenizer length
        // follows, then its six bytes and two Int32 complete-string counts.
        if (change == "version") body[7] = (byte)'2';
        if (change == "route-count") body[136] = 3;
        if (change == "input-count") Array.Clear(body, 128, 4);
        if (change == "utf8") body[122] = 0xFF;
        if (change == "length") { body[120] = 0xFF; body[121] = 0xFF; }
        if (change == "trailing") body = [.. body, 0];
        if (change == "alias-order")
        {
            var first = Find(body, "a-echo"u8); var last = Find(body, "z-echo"u8);
            body[first] = (byte)'z'; body[last] = (byte)'a';
        }
        Denied([.. body, .. SHA256.HashData(body)]);
    }

    private static int Find(byte[] bytes, ReadOnlySpan<byte> value)
    {
        var index = bytes.AsSpan().IndexOf(value); Assert.True(index >= 0); return index;
    }
    private static void Denied(byte[] bytes)
    {
        var error = Assert.Throws<InvalidOperationException>(() => GroupStructuredAdmissionSnapshotCodec.Read(bytes));
        Assert.Equal("Group historical admission metadata is not available.", error.Message); Assert.Null(error.InnerException);
    }
}
