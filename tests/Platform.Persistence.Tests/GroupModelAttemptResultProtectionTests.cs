using System.Security.Cryptography;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupModelAttemptResultProtectionTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
    private static GroupModelAttemptResultProtectionContext Context() => new(
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(),
        new('A', 64), 1, Guid.NewGuid(), new('B', 64));

    [Theory]
    [InlineData(200)]
    [InlineData(66000)]
    public void Encrypted_binary_round_trip_has_fresh_nonce_and_returns_independent_bytes(int length)
    {
        var context = Context();
        var clear = Enumerable.Repeat((byte)37, length).ToArray();
        var protector = new GroupModelAttemptResultProtector();
        var first = protector.Protect(context, clear, Key, "scoped_key");
        var second = protector.Protect(context, clear, Key, "scoped_key");
        Assert.Equal(length + 29, first.Length);
        Assert.False(first.AsSpan().SequenceEqual(second));
        var restored = protector.Unprotect(context, first, Key, "scoped_key");
        Assert.Equal(clear, restored);
        restored[0] ^= 1;
        Assert.Equal(clear, protector.Unprotect(context, first, Key, "scoped_key"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Ciphertext_copied_to_another_scope_or_original_attempt_is_rejected(int field)
    {
        var context = Context();
        var changed = field switch
        {
            0 => context with { Scope = context.Scope with { TenantId = Guid.NewGuid() } },
            1 => context with { Scope = context.Scope with { CompanyId = Guid.NewGuid() } },
            2 => context with { Scope = context.Scope with { SourceBindingId = Guid.NewGuid() } },
            3 => context with { BatchId = Guid.NewGuid() },
            4 => context with { RequestId = Guid.NewGuid() },
            5 => context with { PlanSha256 = new('C', 64) },
            6 => context with { ChunkOrdinal = 2 },
            7 => context with { OperationId = Guid.NewGuid() },
            _ => context with { InputSha256 = new('C', 64) }
        };
        var protector = new GroupModelAttemptResultProtector();
        var envelope = protector.Protect(context, new byte[200], Key, "key_one");
        RequireClosed(() => protector.Unprotect(changed, envelope, Key, "key_one"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(29)]
    [InlineData(228)]
    public void Modified_version_nonce_tag_or_ciphertext_is_rejected(int index)
    {
        var context = Context();
        var protector = new GroupModelAttemptResultProtector();
        var envelope = protector.Protect(context, new byte[200], Key, "key_one");
        envelope[index] ^= 1;
        RequireClosed(() => protector.Unprotect(context, envelope, Key, "key_one"));
    }

    [Fact]
    public void Wrong_key_key_identifier_bounds_and_unprotected_bytes_are_rejected()
    {
        var context = Context();
        var protector = new GroupModelAttemptResultProtector();
        var envelope = protector.Protect(context, new byte[200], Key, "key_one");
        RequireClosed(() => protector.Unprotect(context, envelope, RandomNumberGenerator.GetBytes(32), "key_one"));
        RequireClosed(() => protector.Unprotect(context, envelope, Key, "key_two"));
        RequireClosed(() => protector.Unprotect(context, new byte[200], Key, "key_one"));
        RequireClosed(() => protector.Protect(context, new byte[199], Key, "key_one"));
        RequireClosed(() => protector.Protect(context, new byte[66001], Key, "key_one"));
        RequireClosed(() => protector.Unprotect(context, new byte[66030], Key, "key_one"));
        RequireClosed(() => protector.Protect(context with { ChunkOrdinal = 101 }, new byte[200], Key, "key_one"));
        RequireClosed(() => protector.Protect(context, new byte[200], Key, "private/key"));
        RequireClosed(() => protector.Protect(context, new byte[200], new byte[31], "key_one"));
    }

    private static void RequireClosed(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("Group model attempt protected result is unavailable.", error.Message);
        Assert.Null(error.InnerException);
    }
}
