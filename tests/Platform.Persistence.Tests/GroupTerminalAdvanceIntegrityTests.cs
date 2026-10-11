using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupTerminalAdvanceIntegrityTests
{
    [Fact]
    public void CanonicalBytesAndMacMatchIndependentHkdfAndManualLayout()
    {
        var value = Value(); var rawKey = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        using var key = new GroupSourceKeyMaterial(value.KeyId, rawKey);
        var signed = GroupTerminalAdvanceIntegrity.Sign(value, key); var bytes = signed.Write();
        var body = bytes[..^32];
        Assert.Equal(289 + value.KeyId.Length, body.Length); Assert.Equal("AIOGFAD1"u8.ToArray(), body[..8]);
        Assert.Equal(value.Scope.TenantId.ToByteArray(), body[8..24]);
        Assert.Equal(value.Scope.CompanyId.ToByteArray(), body[24..40]);
        Assert.Equal(value.Scope.SourceBindingId.ToByteArray(), body[40..56]);
        Assert.Equal(value.BatchId.ToByteArray(), body[56..72]);
        Assert.Equal(value.TerminalOperationId.ToByteArray(), body[72..88]); Assert.Equal(value.ServiceId.ToByteArray(), body[88..104]);
        var numbers = new[] { value.PreviousVersion, value.Version, value.AfterSequence, value.ThroughSequence, value.CredentialEpoch,
            value.GrantVersion, value.SourceVersion, value.DeletionGeneration, value.AccountVersion,
            value.TerminalCommittedAtUtc.UtcTicks, value.AdvancedAtUtc.UtcTicks };
        for (var index = 0; index < numbers.Length; index++)
            Assert.Equal(numbers[index], BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(104 + index * 8, 8)));
        Assert.Equal(value.PreviousFingerprint, body[192..224]); Assert.Equal(value.TerminalManifestSha256, body[224..256]);
        Assert.Equal(value.AuthoritySha256, body[256..288]);
        Assert.Equal(value.KeyId.Length, body[288]); Assert.Equal(Encoding.ASCII.GetBytes(value.KeyId), body[289..]);
        var salt = Encoding.ASCII.GetBytes("aioffice-group-frontier-key-v1\0" + value.Scope.TenantId.ToString("D") + "/"
            + value.Scope.CompanyId.ToString("D") + "/" + value.Scope.SourceBindingId.ToString("D"));
        // RFC 5869 extract and the single required expand block, independent
        // of the production HKDF API. Purpose includes no source plaintext.
        var prk = HMACSHA256.HashData(salt, rawKey);
        var info = Encoding.ASCII.GetBytes("aioffice-group-terminal-advance-integrity-v1");
        byte[] expand = [.. info, 1]; var derived = HMACSHA256.HashData(prk, expand);
        Assert.Equal(HMACSHA256.HashData(derived, body), bytes[^32..]);
        Assert.NotEqual(HMACSHA256.HashData(rawKey, body), bytes[^32..]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), signed.Fingerprint);
        Assert.Equal(signed.Fingerprint, GroupTerminalAdvanceIntegrity.Require(bytes, value.Scope, key).Fingerprint);
    }

    [Fact]
    public void EveryCarrierByteIsAuthenticatedAndUnkeyedChecksumsCannotForgeIt()
    {
        var value = Value(); using var key = Key(); var bytes = GroupTerminalAdvanceIntegrity.Sign(value, key).Write();
        for (var index = 0; index < bytes.Length; index++)
        {
            var modified = bytes.ToArray(); modified[index] ^= 1;
            var error = Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Require(modified, value.Scope, key));
            Assert.Equal("Group terminal advance integrity is not available.", error.Message); Assert.Null(error.InnerException);
        }
        var forged = bytes.ToArray(); forged[120] ^= 1; SHA256.HashData(forged.AsSpan(0, forged.Length - 32)).CopyTo(forged, forged.Length - 32);
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Require(forged, value.Scope, key));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("key-id")]
    [InlineData("key-material")]
    [InlineData("disposed")]
    public void WrongScopeAndKeyNeverAuthenticateEvenWhenSameKeyMaterialIsReused(string fault)
    {
        var value = Value(); using var key = Key(); var bytes = GroupTerminalAdvanceIntegrity.Sign(value, key).Write();
        var scope = fault switch
        {
            "tenant" => value.Scope with { TenantId = Guid.NewGuid() },
            "company" => value.Scope with { CompanyId = Guid.NewGuid() },
            "binding" => value.Scope with { SourceBindingId = Guid.NewGuid() },
            _ => value.Scope
        };
        using var wrong = fault == "key-material" ? new GroupSourceKeyMaterial(value.KeyId, Enumerable.Repeat((byte)0x66, 32).ToArray())
            : fault == "key-id" ? new GroupSourceKeyMaterial("another-key", key.Key.ToArray()) : Key();
        if (fault == "disposed") wrong.Dispose();
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Require(bytes, scope, wrong));
        if (fault is "tenant" or "company" or "binding") Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.PeekKeyId(bytes, scope));
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("service")]
    [InlineData("version")]
    [InlineData("previous-version")]
    [InlineData("after")]
    [InlineData("through")]
    [InlineData("raw-bound")]
    [InlineData("previous-bytes")]
    [InlineData("genesis-digest")]
    [InlineData("terminal-bytes")]
    [InlineData("terminal-zero")]
    [InlineData("authority-bytes")]
    [InlineData("authority-zero")]
    [InlineData("credential")]
    [InlineData("grant")]
    [InlineData("source")]
    [InlineData("deletion")]
    [InlineData("account")]
    [InlineData("before-terminal")]
    [InlineData("terminal-offset")]
    [InlineData("advance-offset")]
    [InlineData("key-empty")]
    [InlineData("key-long")]
    [InlineData("key-unicode")]
    public void InvalidMetadataCannotBeSigned(string fault)
    {
        var v = Value(); using var key = Key();
        v = fault switch
        {
            "batch" => v with { BatchId = Guid.Empty },
            "operation" => v with { TerminalOperationId = Guid.Empty },
            "service" => v with { ServiceId = Guid.Empty },
            "version" => v with { Version = 0 },
            "previous-version" => v with { PreviousVersion = 1 },
            "after" => v with { AfterSequence = 1 },
            "through" => v with { ThroughSequence = 0 },
            "raw-bound" => v with { ThroughSequence = 501 },
            "previous-bytes" => v with { PreviousFingerprint = new byte[33] },
            "genesis-digest" => v with { PreviousFingerprint = SHA256.HashData([1]) },
            "terminal-bytes" => v with { TerminalManifestSha256 = new byte[31] },
            "terminal-zero" => v with { TerminalManifestSha256 = new byte[32] },
            "authority-bytes" => v with { AuthoritySha256 = new byte[33] },
            "authority-zero" => v with { AuthoritySha256 = new byte[32] },
            "credential" => v with { CredentialEpoch = 0 },
            "grant" => v with { GrantVersion = 0 },
            "source" => v with { SourceVersion = 0 },
            "deletion" => v with { DeletionGeneration = -1 },
            "account" => v with { AccountVersion = 0 },
            "before-terminal" => v with { AdvancedAtUtc = v.TerminalCommittedAtUtc.AddTicks(-1) },
            "terminal-offset" => v with { TerminalCommittedAtUtc = v.TerminalCommittedAtUtc.ToOffset(TimeSpan.FromHours(7)) },
            "advance-offset" => v with { AdvancedAtUtc = v.AdvancedAtUtc.ToOffset(TimeSpan.FromHours(7)) },
            "key-empty" => v with { KeyId = "" },
            "key-long" => v with { KeyId = new string('a', 65) },
            "key-unicode" => v with { KeyId = "khóa" },
            _ => throw new InvalidOperationException()
        };
        var error = Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Sign(v, key));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void SuccessorCarriesExactPredecessorButShapeIsNotVerificationOfThatPredecessor()
    {
        var v = Value(); using var key = Key(); var first = GroupTerminalAdvanceIntegrity.Sign(v, key);
        var second = GroupTerminalAdvanceIntegrity.Sign(v with
        {
            BatchId = Guid.NewGuid(),
            TerminalOperationId = Guid.NewGuid(),
            PreviousVersion = 1,
            Version = 2,
            AfterSequence = 2,
            ThroughSequence = 4,
            PreviousFingerprint = Convert.FromHexString(first.Fingerprint)
        }, key);
        Assert.Equal(first.Fingerprint, Convert.ToHexString(second.Metadata.PreviousFingerprint));
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Sign(second.Metadata with { AfterSequence = 0 }, key));
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Sign(second.Metadata with { PreviousVersion = 0 }, key));
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Sign(second.Metadata with { PreviousFingerprint = new byte[32] }, key));
        // A host with a key could sign arbitrary well-shaped metadata. The
        // owned store must verify predecessor and terminal graphs before Sign.
        Assert.NotEqual(second.Fingerprint, GroupTerminalAdvanceIntegrity.Sign(second.Metadata with { PreviousFingerprint = SHA256.HashData([99]) }, key).Fingerprint);
    }

    [Fact]
    public void SignedObjectAndReadsOwnAllMetadataAndEncodedBytes()
    {
        var value = Value(); using var key = Key(); var signed = GroupTerminalAdvanceIntegrity.Sign(value, key); var original = signed.Write();
        value.PreviousFingerprint[0] = 1; value.TerminalManifestSha256[0] ^= 1; value.AuthoritySha256[0] ^= 1;
        var decoded = signed.Metadata; decoded.PreviousFingerprint[0] = 1; decoded.TerminalManifestSha256[0] ^= 1; decoded.AuthoritySha256[0] ^= 1;
        var output = signed.Write(); output[0] = 0;
        Assert.Equal(original, signed.Write());
        var verified = GroupTerminalAdvanceIntegrity.Require(original, signed.Metadata.Scope, key); original[0] = 0;
        Assert.Equal(signed.Write(), verified.Write());
    }

    [Fact]
    public void RetainedKeySurvivesIndependentWorkerAndGrantRotationWithoutFallback()
    {
        var value = Value(); using var old = Key(); var bytes = GroupTerminalAdvanceIntegrity.Sign(value, old).Write();
        Assert.Equal(old.KeyId, GroupTerminalAdvanceIntegrity.PeekKeyId(bytes, value.Scope));
        using var current = new GroupSourceKeyMaterial("new-key", Enumerable.Repeat((byte)0x77, 32).ToArray());
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Require(bytes, value.Scope, current));
        var retained = GroupTerminalAdvanceIntegrity.Require(bytes, value.Scope, old);
        var newValue = retained.Metadata with
        {
            BatchId = Guid.NewGuid(),
            TerminalOperationId = Guid.NewGuid(),
            PreviousVersion = 1,
            Version = 2,
            AfterSequence = 2,
            ThroughSequence = 4,
            PreviousFingerprint = Convert.FromHexString(retained.Fingerprint),
            CredentialEpoch = 2,
            GrantVersion = 3,
            SourceVersion = 4,
            KeyId = current.KeyId
        };
        var next = GroupTerminalAdvanceIntegrity.Sign(newValue, current);
        Assert.Equal(next.Fingerprint, GroupTerminalAdvanceIntegrity.Require(next.Write(), value.Scope, current).Fingerprint);
        Assert.Equal(retained.Fingerprint, Convert.ToHexString(next.Metadata.PreviousFingerprint));
    }

    [Fact]
    public void TruncationTrailingBytesInvalidKeyEncodingAndHugeEnvelopesRefuse()
    {
        var value = Value(); using var key = Key(); var bytes = GroupTerminalAdvanceIntegrity.Sign(value, key).Write();
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Require(bytes[..length], value.Scope, key));
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Require([.. bytes, 0], value.Scope, key));
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Require(new byte[1_000_000], value.Scope, key));
        var bad = bytes.ToArray(); bad[289] = 0xFF;
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.PeekKeyId(bad, value.Scope));
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Sign(value with { PreviousFingerprint = null! }, key));
        Assert.Throws<InvalidOperationException>(() => GroupTerminalAdvanceIntegrity.Sign(null!, key));
    }

    [Fact]
    public void LargestRangeKeyAndVersionRemainBoundedWithoutOverflow()
    {
        var value = Value() with
        {
            KeyId = new string('a', 64),
            PreviousVersion = long.MaxValue - 1,
            Version = long.MaxValue,
            AfterSequence = long.MaxValue - 500,
            ThroughSequence = long.MaxValue,
            PreviousFingerprint = SHA256.HashData([1])
        };
        using var key = new GroupSourceKeyMaterial(value.KeyId, Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var signed = GroupTerminalAdvanceIntegrity.Sign(value, key);
        Assert.Equal(GroupTerminalAdvanceIntegrity.MaximumBytes, signed.Write().Length);
        Assert.Equal(long.MaxValue, GroupTerminalAdvanceIntegrity.Require(signed.Write(), value.Scope, key).Metadata.Version);
        var first = Value() with { KeyId = "a" }; using var shortKey = new GroupSourceKeyMaterial("a", key.Key.ToArray());
        Assert.Equal(GroupTerminalAdvanceIntegrity.MinimumBytes, GroupTerminalAdvanceIntegrity.Sign(first, shortKey).Write().Length);
    }

    private static GroupTerminalAdvanceMetadata Value() => new(new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"), Guid.Parse("20000000-0000-0000-0000-000000000002"),
        Guid.Parse("30000000-0000-0000-0000-000000000003")),
        Guid.Parse("40000000-0000-0000-0000-000000000004"), Guid.Parse("50000000-0000-0000-0000-000000000005"),
        0, 1, 0, 2, new byte[32], SHA256.HashData([42]), new(2026, 10, 11, 4, 0, 0, TimeSpan.Zero),
        new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero), Guid.Parse("60000000-0000-0000-0000-000000000006"),
        1, 1, 1, 0, 1, SHA256.HashData([99]), "owned-key");
    private static GroupSourceKeyMaterial Key() => new("owned-key", Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
}
