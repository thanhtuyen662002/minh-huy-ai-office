using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupSourceKeyProviderTests
{
    [Fact]
    public async Task OnlyExactEnrolledSourceCanResolveActiveOrRetainedKeyWithoutFallback()
    {
        var source = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var resolver = new OwnedResolver();
        var bindings = new List<GroupSourceKeyBinding>
        {
            new(source, "key-old", SecretReference.Parse("secretref://env/OWNED_OLD"), false),
            new(source, "key-new", SecretReference.Parse("secretref://env/OWNED_NEW"), true)
        };
        var provider = new ConfiguredGroupSourceKeyProvider(new([resolver]), bindings);
        bindings.Clear();
        using var current = await provider.ResolveWriteAsync(source); Assert.Equal("key-new", current.KeyId);
        using var old = await provider.ResolveReadAsync(source, "key-old"); Assert.Equal("key-old", old.KeyId);
        Assert.NotEqual(current.Key.ToArray(), old.Key.ToArray());
        foreach (var foreign in new[] { source with { TenantId = Guid.NewGuid() }, source with { CompanyId = Guid.NewGuid() }, source with { SourceBindingId = Guid.NewGuid() } })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.ResolveWriteAsync(foreign));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.ResolveReadAsync(foreign, "key-old"));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.ResolveReadAsync(source, "key-old "));
        Assert.Equal(new[] { "OWNED_NEW", "OWNED_OLD" }, resolver.Requested);
    }

    [Fact]
    public void DuplicateKeyAndMultipleWriteKeyEnrollmentIsRefused()
    {
        var source = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var binding = new GroupSourceKeyBinding(source, "owned", SecretReference.Parse("secretref://env/OWNED_NEW"), true);
        var secrets = new CompositeSecretResolver([new OwnedResolver()]);
        Assert.Throws<InvalidOperationException>(() => new ConfiguredGroupSourceKeyProvider(secrets, [binding, binding]));
        Assert.Throws<InvalidOperationException>(() => new ConfiguredGroupSourceKeyProvider(secrets, [binding, binding with { KeyId = "other" }]));
        Assert.Throws<InvalidOperationException>(() => new ConfiguredGroupSourceKeyProvider(secrets, [binding with { KeyId = "bad/key" }]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("malformed")]
    [InlineData("whitespace")]
    public async Task InvalidSecretMaterialReturnsOnlyBoundedUnavailableError(string mode)
    {
        var source = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var provider = new ConfiguredGroupSourceKeyProvider(new([new OwnedResolver(mode)]),
            [new(source, "owned", SecretReference.Parse("secretref://env/OWNED_NEW"), true)]);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.ResolveWriteAsync(source));
        Assert.Equal("Group source key is unavailable.", failure.Message); Assert.Null(failure.InnerException);
    }

    [Fact]
    public void MaterialOwnsItsKeyAndDisposedKeyIsInaccessible()
    {
        var input = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var material = new GroupSourceKeyMaterial("owned", input); input[0] = 0;
        Assert.Equal(0x55, material.Key[0]); material.Dispose(); material.Dispose();
        Assert.Throws<ObjectDisposedException>(() => material.Key.ToArray());
        Assert.Throws<InvalidOperationException>(() => new GroupSourceKeyMaterial("owned", null!));
    }

    private sealed class OwnedResolver(string? mode = null) : ISecretResolver
    {
        internal readonly List<string> Requested = [];
        public string Provider => "env";
        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            Requested.Add(reference.Resource);
            if (mode == "missing") throw new InvalidOperationException("Private owned credential diagnostic must be masked");
            var value = mode switch
            {
                "short" => Convert.ToBase64String(new byte[16]),
                "malformed" => new string('!', 44),
                "whitespace" => " " + Convert.ToBase64String(new byte[32]),
                _ => Convert.ToBase64String(Enumerable.Repeat(reference.Resource == "OWNED_NEW" ? (byte)0x31 : (byte)0x32, 32).ToArray())
            };
            return ValueTask.FromResult(value);
        }
    }
}
