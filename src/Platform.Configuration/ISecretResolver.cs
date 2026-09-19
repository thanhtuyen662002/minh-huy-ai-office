namespace MinhHuy.AIOffice.Platform.Configuration;

public interface ISecretResolver
{
    string Provider { get; }

    ValueTask<string> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);
}
