namespace MinhHuy.AIOffice.Platform.Configuration;

public sealed class EnvironmentVariableSecretResolver : ISecretResolver
{
    public const string ProviderName = "env";

    public string Provider => ProviderName;

    public ValueTask<string> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        if (!reference.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Resolver '{Provider}' cannot resolve provider '{reference.Provider}'.");
        }

        if (!IsPortableEnvironmentVariableName(reference.Resource))
        {
            throw new FormatException(
                "Environment secret references must target a portable environment variable name containing only letters, digits, and underscores.");
        }

        var value = Environment.GetEnvironmentVariable(reference.Resource);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Secret environment variable '{reference.Resource}' is not set.");
        }

        return ValueTask.FromResult(value);
    }

    private static bool IsPortableEnvironmentVariableName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        return name.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character == '_');
    }
}
