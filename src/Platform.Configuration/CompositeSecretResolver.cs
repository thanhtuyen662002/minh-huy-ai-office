namespace MinhHuy.AIOffice.Platform.Configuration;

public sealed class CompositeSecretResolver
{
    private readonly IReadOnlyDictionary<string, ISecretResolver> _resolvers;

    public CompositeSecretResolver(IEnumerable<ISecretResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);

        var byProvider = new Dictionary<string, ISecretResolver>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolver in resolvers)
        {
            ArgumentNullException.ThrowIfNull(resolver);

            if (string.IsNullOrWhiteSpace(resolver.Provider))
            {
                throw new ArgumentException("Secret resolver provider names cannot be empty.", nameof(resolvers));
            }

            if (!byProvider.TryAdd(resolver.Provider, resolver))
            {
                throw new ArgumentException(
                    $"More than one secret resolver is registered for provider '{resolver.Provider}'.",
                    nameof(resolvers));
            }
        }

        _resolvers = byProvider;
    }

    public ValueTask<string> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!_resolvers.TryGetValue(reference.Provider, out var resolver))
        {
            throw new NotSupportedException(
                $"No secret resolver is registered for provider '{reference.Provider}'.");
        }

        return resolver.ResolveAsync(reference, cancellationToken);
    }
}
