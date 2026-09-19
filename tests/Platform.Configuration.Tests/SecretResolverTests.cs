using MinhHuy.AIOffice.Platform.Configuration;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Configuration.Tests;

public sealed class SecretResolverTests
{
    [Fact]
    public async Task Environment_resolver_reads_secret_only_at_runtime()
    {
        var variableName = $"AIOFFICE_TEST_SECRET_{Guid.NewGuid():N}";
        const string secretValue = "runtime-only-secret";

        try
        {
            Environment.SetEnvironmentVariable(variableName, secretValue);

            var reference = SecretReference.Parse($"secretref://env/{variableName}");
            var resolver = new CompositeSecretResolver(
                new ISecretResolver[] { new EnvironmentVariableSecretResolver() });

            var resolved = await resolver.ResolveAsync(reference);

            Assert.Equal(secretValue, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task Environment_resolver_rejects_non_portable_variable_names()
    {
        var reference = SecretReference.Parse("secretref://env/path/with/slashes");
        var resolver = new EnvironmentVariableSecretResolver();

        await Assert.ThrowsAsync<FormatException>(
            async () => await resolver.ResolveAsync(reference));
    }

    [Fact]
    public async Task Composite_resolver_fails_closed_for_unregistered_provider()
    {
        var reference = SecretReference.Parse("secretref://vault/team/secret");
        var resolver = new CompositeSecretResolver(
            new ISecretResolver[] { new EnvironmentVariableSecretResolver() });

        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await resolver.ResolveAsync(reference));
    }
}
