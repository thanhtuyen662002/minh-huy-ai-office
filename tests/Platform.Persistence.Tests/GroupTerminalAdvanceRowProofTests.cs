using System.Security.Cryptography;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupTerminalAdvanceRowProofTests
{
    [Fact]
    public void EverySqlCarrierColumnMustMatchAuthenticatedBytesIncludingAllRootNullFields()
    {
        var value = Value(); using var key = Key(); var signed = GroupTerminalAdvanceIntegrity.Sign(value, key);
        var properties = typeof(GroupTerminalAdvanceRecord).GetProperties(); Assert.Equal(26, properties.Length);
        foreach (var property in properties)
        {
            var row = GroupTerminalAdvanceRowProof.Stage(signed, null); var current = property.GetValue(row);
            object changed = current switch
            {
                Guid => Guid.NewGuid(),
                long number => number + 1,
                int number => number + 1,
                string text => text + "changed",
                byte[] bytes => ChangedBytes(bytes),
                DateTimeOffset time => time.AddTicks(1),
                null when property.PropertyType == typeof(long?) => 1L,
                null when property.PropertyType == typeof(DateTimeOffset?) => value.AdvancedAtUtc,
                null when property.PropertyType == typeof(byte[]) => SHA256.HashData([123]),
                _ => throw new InvalidOperationException(property.Name)
            };
            property.SetValue(row, changed);
            var error = Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceRowProof.Require(row, value.Scope, key, null));
            Assert.Equal("Group terminal advance row proof is not available.", error.Message); Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void GenuineRetainedKeySuccessorBindsActualPredecessorAndDetachedSqlCarrier()
    {
        var value = Value(); using var old = Key(); using var current = new GroupSourceKeyMaterial("rotated-key", Enumerable.Repeat((byte)0x77, 32).ToArray());
        var root = GroupTerminalAdvanceIntegrity.Require(GroupTerminalAdvanceIntegrity.Sign(value, old).Write(), value.Scope, old);
        var nextValue = Next(root, current.KeyId); var next = GroupTerminalAdvanceIntegrity.Sign(nextValue, current);
        var row = GroupTerminalAdvanceRowProof.Stage(next, root);
        var observed = GroupTerminalAdvanceRowProof.Require(row, value.Scope, current, root);
        Assert.Equal(next.Fingerprint, observed.Fingerprint); Assert.Equal(root.Fingerprint, Convert.ToHexString(row.PreviousFingerprint!));
        Assert.Equal(root.Metadata.AdvancedAtUtc, row.PreviousAdvancedAtUtc);
        row.Envelope[0] ^= 1; row.Fingerprint[0] ^= 1; row.TerminalManifestSha256[0] ^= 1; row.AuthoritySha256[0] ^= 1;
        row.PreviousFingerprint![0] ^= 1; row.Version = 100;
        Assert.Equal(next.Write(), observed.Write()); Assert.Equal(next.Fingerprint, observed.Fingerprint);
        var metadata = observed.Metadata;
        Assert.Equal(nextValue.Scope, metadata.Scope); Assert.Equal(nextValue.BatchId, metadata.BatchId);
        Assert.Equal(2, metadata.Version); Assert.Equal(2, metadata.AfterSequence); Assert.Equal(4, metadata.ThroughSequence);
        Assert.Equal(current.KeyId, metadata.KeyId); Assert.Equal(nextValue.AdvancedAtUtc, metadata.AdvancedAtUtc);
        Assert.Equal(nextValue.PreviousFingerprint, metadata.PreviousFingerprint);
        Assert.Equal(nextValue.TerminalManifestSha256, metadata.TerminalManifestSha256); Assert.Equal(nextValue.AuthoritySha256, metadata.AuthoritySha256);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("through")]
    [InlineData("clock")]
    public void SignedSuccessorCannotSubstituteOrSkipAuthenticatedPredecessor(string fault)
    {
        var value = Value(); using var key = Key(); var root = GroupTerminalAdvanceIntegrity.Sign(value, key);
        var nextValue = Next(root, key.KeyId); GroupTerminalAdvanceIntegrity? predecessor = root;
        switch (fault)
        {
            case "missing": predecessor = null; break;
            case "foreign": predecessor = GroupTerminalAdvanceIntegrity.Sign(value with { Scope = value.Scope with { SourceBindingId = Guid.NewGuid() } }, key); break;
            case "hash": nextValue = nextValue with { PreviousFingerprint = SHA256.HashData([13]) }; break;
            case "version": nextValue = nextValue with { PreviousVersion = 2, Version = 3 }; break;
            case "through": nextValue = nextValue with { AfterSequence = 3, ThroughSequence = 5 }; break;
            case "clock": nextValue = nextValue with { AdvancedAtUtc = value.AdvancedAtUtc.AddTicks(-1) }; break;
        }
        var next = GroupTerminalAdvanceIntegrity.Sign(nextValue, key);
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceRowProof.Stage(next, predecessor));
        var row = GroupTerminalAdvanceRowProof.Stage(GroupTerminalAdvanceIntegrity.Sign(Next(root, key.KeyId), key), root);
        row.Envelope = next.Write(); row.Fingerprint = Convert.FromHexString(next.Fingerprint);
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceRowProof.Require(row, value.Scope, key, predecessor));
    }

    [Theory]
    [InlineData("tiny")]
    [InlineData("huge")]
    [InlineData("null-envelope")]
    [InlineData("null-terminal")]
    [InlineData("null-authority")]
    [InlineData("null-fingerprint")]
    [InlineData("oversized-predecessor")]
    [InlineData("null-row")]
    [InlineData("foreign-scope")]
    [InlineData("wrong-key")]
    [InlineData("utc-offset")]
    [InlineData("extra-predecessor")]
    public void MalformedCarriersKeysScopeUtcAndUnexpectedRootPredecessorRefusePrivately(string fault)
    {
        var value = Value(); using var key = Key(); using var wrong = new GroupSourceKeyMaterial(key.KeyId, Enumerable.Repeat((byte)0x99, 32).ToArray());
        var signed = GroupTerminalAdvanceIntegrity.Sign(value, key); var row = GroupTerminalAdvanceRowProof.Stage(signed, null);
        var scope = value.Scope; var selected = key; GroupTerminalAdvanceIntegrity? predecessor = null;
        switch (fault)
        {
            case "tiny": row.Envelope = new byte[321]; break;
            case "huge": row.Envelope = new byte[1_000_000]; break;
            case "null-envelope": row.Envelope = null!; break;
            case "null-terminal": row.TerminalManifestSha256 = null!; break;
            case "null-authority": row.AuthoritySha256 = null!; break;
            case "null-fingerprint": row.Fingerprint = null!; break;
            case "oversized-predecessor": row.PreviousFingerprint = new byte[1_000_000]; break;
            case "null-row": row = null!; break;
            case "foreign-scope": scope = scope with { CompanyId = Guid.NewGuid() }; break;
            case "wrong-key": selected = wrong; break;
            case "utc-offset": row.AdvancedAtUtc = row.AdvancedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "extra-predecessor": predecessor = signed; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceRowProof.Require(row, scope, selected, predecessor));
        Assert.Equal("Group terminal advance row proof is not available.", error.Message); Assert.Null(error.InnerException);
    }

    private static byte[] ChangedBytes(byte[] bytes) { var copy = bytes.ToArray(); copy[0] ^= 1; return copy; }
    private static GroupSourceKeyMaterial Key() => new("owned-key", Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    private static GroupTerminalAdvanceMetadata Value() => new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(),
        0, 1, 0, 2, new byte[32], SHA256.HashData([42]), new(2026, 10, 11, 4, 0, 0, TimeSpan.Zero),
        new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero), Guid.NewGuid(), 1, 1, 1, 0, 1, SHA256.HashData([99]), "owned-key");
    private static GroupTerminalAdvanceMetadata Next(GroupTerminalAdvanceIntegrity root, string key) => root.Metadata with
    {
        BatchId = Guid.NewGuid(),
        TerminalOperationId = Guid.NewGuid(),
        PreviousVersion = 1,
        Version = 2,
        AfterSequence = 2,
        ThroughSequence = 4,
        PreviousFingerprint = Convert.FromHexString(root.Fingerprint),
        AdvancedAtUtc = root.Metadata.AdvancedAtUtc.AddTicks(1),
        KeyId = key
    };
}
