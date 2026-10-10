using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupSourceContentProtectorTests
{
    private readonly GroupSourceContentProtector protector = new();
    private readonly byte[] key = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private readonly GroupSourceContentContext context = new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), 1, 2, 3);

    [Fact]
    public void ExactOriginalUnicodeWhitespaceAndEmptySourceAreProtectedWithRandomNonces()
    {
        foreach (var text in new[] { "", " ", "\uFEFF Công việc 😀\n  ", new string('x', 8000) })
        {
            var first = protector.Protect(context, text, key, "key-v1");
            var second = protector.Protect(context, text, key, "key-v1");
            Assert.NotEqual(first, second);
            Assert.Equal(text, protector.Unprotect(context, first, key, "key-v1"));
            Assert.InRange(first.Length, 29, GroupSourceContentProtector.MaximumEnvelopeLength);
        }
    }

    [Fact]
    public void EveryScopeRevisionAndDeletionChangeInvalidatesProtectedSource()
    {
        var envelope = protector.Protect(context, "owned fixture source", key, "key-v1");
        var changed = new[]
        {
            context with { Source = context.Source with { TenantId = Guid.NewGuid() } },
            context with { Source = context.Source with { CompanyId = Guid.NewGuid() } },
            context with { Source = context.Source with { SourceBindingId = Guid.NewGuid() } },
            context with { MessageId = Guid.NewGuid() }, context with { Revision = 2 },
            context with { SourceVersion = 3 }, context with { DeletionGeneration = 4 }
        };
        foreach (var other in changed)
            Assert.Equal("Group source content is unavailable.", Assert.Throws<InvalidOperationException>(() => protector.Unprotect(other, envelope, key, "key-v1")).Message);
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(context, envelope, key, "key-v2"));
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(context, envelope, new byte[32], "key-v1"));
        Assert.Equal("owned fixture source", protector.Unprotect(context, envelope, key, "key-v1"));
    }

    [Fact]
    public void TamperingEveryEnvelopeRegionIsRefusedWithoutSourceInErrors()
    {
        var envelope = protector.Protect(context, "private-test-string", key, "key-v1");
        foreach (var position in new[] { 0, 1, 12, 13, 28, 29, envelope.Length - 1 })
        {
            var changed = envelope.ToArray(); changed[position] ^= 1;
            var failure = Assert.Throws<InvalidOperationException>(() => protector.Unprotect(context, changed, key, "key-v1"));
            Assert.DoesNotContain("private-test-string", failure.Message);
        }
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(context, envelope.AsSpan(0, 28), key, "key-v1"));
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(context, new byte[65537], key, "key-v1"));
    }

    [Fact]
    public void AdjacentOverflowMalformedUtf16InvalidScopeAndWrongKeyShapeAreRefused()
    {
        Assert.Throws<InvalidOperationException>(() => protector.Protect(context, new string('x', 8001), key, "key-v1"));
        Assert.Throws<InvalidOperationException>(() => protector.Protect(context, new string((char)0xD800, 1), key, "key-v1"));
        Assert.Throws<InvalidOperationException>(() => protector.Protect(context with { Revision = 0 }, "x", key, "key-v1"));
        Assert.Throws<InvalidOperationException>(() => protector.Protect(context with { Source = context.Source with { TenantId = Guid.Empty } }, "x", key, "key-v1"));
        Assert.Throws<InvalidOperationException>(() => protector.Protect(context, "x", new byte[16], "key-v1"));
        Assert.Throws<InvalidOperationException>(() => protector.Protect(context, "x", key, "key/v1"));
    }

    [Fact]
    public void AuthenticatedOversizedOrInvalidUtf8CleartextIsStillRefused()
    {
        var aad = Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-source-v1\0{context.Source.TenantId:D}/{context.Source.CompanyId:D}/{context.Source.SourceBindingId:D}/{context.MessageId:D}/{context.Revision}/{context.SourceVersion}/{context.DeletionGeneration}/key-v1"));
        foreach (var clear in new[] { Encoding.UTF8.GetBytes(new string('x', 8001)), new byte[] { 0xFF } })
        {
            var envelope = new byte[29 + clear.Length]; envelope[0] = 1;
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(envelope.AsSpan(1, 12), clear, envelope.AsSpan(29), envelope.AsSpan(13, 16), aad);
            Assert.Throws<InvalidOperationException>(() => protector.Unprotect(context, envelope, key, "key-v1"));
        }
    }
}
