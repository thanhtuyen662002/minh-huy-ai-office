using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupBrainContentProtectorTests
{
    private readonly GroupBrainContentProtector protector = new();
    private readonly byte[] key = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private readonly GroupBrainContentContext context = new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
        GroupBrainContentKind.RequestRevision, Guid.NewGuid(), 1, 2, 3);

    [Theory]
    [InlineData(GroupBrainContentKind.RequestRevision)]
    [InlineData(GroupBrainContentKind.GlossaryRevision)]
    public void OriginalPrivateUnicodeAndWhitespaceRoundTripWithFreshNonces(GroupBrainContentKind kind)
    {
        var current = context with { Kind = kind };
        const string text = "{\"customer\":\"\uFEFF Tồn kho 😀\\n\",\"missing\":[\"Mã chứng từ\"]}";
        var first = protector.Protect(current, text, key, "key-v1");
        var second = protector.Protect(current, text, key, "key-v1");
        Assert.NotEqual(first, second);
        Assert.Equal(text, protector.Unprotect(current, first, key, "key-v1"));
        Assert.Equal(text, protector.Unprotect(current, second, key, "key-v1"));
    }

    [Fact]
    public void EveryScopeKindRecordRevisionAndGenerationIsAuthenticated()
    {
        var envelope = protector.Protect(context, "owned-private-note", key, "key-v1");
        var changed = new[]
        {
            context with { Source = context.Source with { TenantId = Guid.NewGuid() } },
            context with { Source = context.Source with { CompanyId = Guid.NewGuid() } },
            context with { Source = context.Source with { SourceBindingId = Guid.NewGuid() } },
            context with { Kind = GroupBrainContentKind.GlossaryRevision },
            context with { RecordId = Guid.NewGuid() }, context with { Revision = 2 },
            context with { SourceVersion = 3 }, context with { DeletionGeneration = 4 }
        };
        foreach (var other in changed) Refused(() => protector.Unprotect(other, envelope, key, "key-v1"));
        Refused(() => protector.Unprotect(context, envelope, key, "key-v2"));
        Refused(() => protector.Unprotect(context, envelope, new byte[32], "key-v1"));
        Assert.Equal("owned-private-note", protector.Unprotect(context, envelope, key, "key-v1"));
    }

    [Fact]
    public void BrainAndOriginalSourceEnvelopesCannotSubstituteForEachOther()
    {
        var sourceContext = new GroupSourceContentContext(context.Source, context.RecordId, context.Revision, context.SourceVersion, context.DeletionGeneration);
        var source = new GroupSourceContentProtector();
        var original = source.Protect(sourceContext, "owned-original", key, "key-v1");
        Refused(() => protector.Unprotect(context, original, key, "key-v1"));
        var note = protector.Protect(context, "owned-note", key, "key-v1");
        Assert.Throws<InvalidOperationException>(() => source.Unprotect(sourceContext, note, key, "key-v1"));
        Assert.Equal("owned-original", source.Unprotect(sourceContext, original, key, "key-v1"));
        Assert.Equal("owned-note", protector.Unprotect(context, note, key, "key-v1"));
    }

    [Fact]
    public void ExactUtf8ByteLimitIncludesUnicodeAndRejectsAdjacentOverflowBeforeEncryption()
    {
        foreach (var text in new[] { new string('x', 64000), new string('界', 21333) + "x" })
        {
            Assert.Equal(64000, Encoding.UTF8.GetByteCount(text));
            var envelope = protector.Protect(context, text, key, "key-v1");
            Assert.Equal(GroupBrainContentProtector.MaximumEnvelopeLength, envelope.Length);
            Assert.Equal(text, protector.Unprotect(context, envelope, key, "key-v1"));
            Refused(() => protector.Protect(context, text + "x", key, "key-v1"));
        }
        Refused(() => protector.Protect(context, new string('界', 21334), key, "key-v1"));
        Refused(() => protector.Protect(context, new string((char)0xD800, 1), key, "key-v1"));
        Refused(() => protector.Protect(context, "", key, "key-v1"));
        Refused(() => protector.Protect(context, null!, key, "key-v1"));
    }

    [Fact]
    public void AllEnvelopeRegionsAndInvalidEnvelopeLengthsFailWithoutPrivateDiagnostics()
    {
        var envelope = protector.Protect(context, "owned-private-note", key, "key-v1");
        foreach (var position in new[] { 0, 1, 12, 13, 28, 29, envelope.Length - 1 })
        {
            var changed = envelope.ToArray(); changed[position] ^= 1;
            Refused(() => protector.Unprotect(context, changed, key, "key-v1"));
        }
        foreach (var length in new[] { 0, 1, 28, 29, GroupBrainContentProtector.MaximumEnvelopeLength + 1 })
            Refused(() => protector.Unprotect(context, new byte[length], key, "key-v1"));
        Refused(() => protector.Unprotect(context, envelope.AsSpan(0, envelope.Length - 1), key, "key-v1"));
    }

    [Fact]
    public void InvalidContextAndKeyShapesFailClosed()
    {
        var invalid = new[]
        {
            context with { Source = null! }, context with { Kind = (GroupBrainContentKind)0 },
            context with { Kind = (GroupBrainContentKind)3 }, context with { RecordId = Guid.Empty },
            context with { Revision = 0 }, context with { Revision = -1 }, context with { SourceVersion = 0 },
            context with { DeletionGeneration = -1 },
            context with { Source = context.Source with { TenantId = Guid.Empty } },
            context with { Source = context.Source with { CompanyId = Guid.Empty } },
            context with { Source = context.Source with { SourceBindingId = Guid.Empty } }, null!
        };
        foreach (var current in invalid)
        {
            Refused(() => protector.Protect(current, "owned-private-note", key, "key-v1"));
            Refused(() => protector.Unprotect(current, new byte[30], key, "key-v1"));
        }
        foreach (var length in new[] { 0, 16, 31, 33 })
            Refused(() => protector.Protect(context, "owned-private-note", new byte[length], "key-v1"));
        foreach (var keyId in new[] { null!, "", "key/v1", "key\0v1", "clé-v1", new string('a', 65) })
            Refused(() => protector.Protect(context, "owned-private-note", key, keyId));
        var valid = protector.Protect(context with { DeletionGeneration = 0 }, "owned-private-note", key, new string('a', 64));
        Assert.Equal("owned-private-note", protector.Unprotect(context with { DeletionGeneration = 0 }, valid, key, new string('a', 64)));
    }

    [Fact]
    public void AuthenticatedInvalidUtf8IsStillRefusedAndAadIsCultureIndependent()
    {
        var aad = Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-brain-v1\0{context.Source.TenantId:D}/{context.Source.CompanyId:D}/{context.Source.SourceBindingId:D}/{(int)context.Kind}/{context.RecordId:D}/{context.Revision}/{context.SourceVersion}/{context.DeletionGeneration}/key-v1"));
        var invalidEnvelope = new byte[30]; invalidEnvelope[0] = 1;
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(invalidEnvelope.AsSpan(1, 12), new byte[] { 0xFF }, invalidEnvelope.AsSpan(29), invalidEnvelope.AsSpan(13, 16), aad);
        Refused(() => protector.Unprotect(context, invalidEnvelope, key, "key-v1"));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var envelope = protector.Protect(context, "owned-private-note", key, "key-v1");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("owned-private-note", protector.Unprotect(context, envelope, key, "key-v1"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Refused(Action action)
    {
        var failure = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("Group brain content is unavailable.", failure.Message);
        Assert.Null(failure.InnerException);
    }
}
