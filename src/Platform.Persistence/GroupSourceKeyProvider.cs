using System.Security.Cryptography;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupSourceKeyBinding(GroupScope Source, string KeyId, SecretReference Reference, bool IsWriteKey);

public sealed class GroupSourceKeyMaterial : IDisposable
{
    private readonly byte[] key;
    private bool disposed;
    public GroupSourceKeyMaterial(string id, byte[] value)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || value is null || value.Length != 32)
            throw new InvalidOperationException("Group source key is unavailable.");
        KeyId = id; key = value.ToArray();
    }
    public string KeyId { get; }
    internal ReadOnlySpan<byte> Key => !disposed ? key : throw new ObjectDisposedException(nameof(GroupSourceKeyMaterial));
    public void Dispose() { CryptographicOperations.ZeroMemory(key); disposed = true; }
}

public interface IGroupSourceKeyProvider
{
    ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default);
    ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default);
}

// Enrollment/configuration supplies exact authorized scopes and retained keys.
// There is no company/global fallback or request-supplied secret reference.
public sealed class ConfiguredGroupSourceKeyProvider : IGroupSourceKeyProvider
{
    private readonly CompositeSecretResolver secrets;
    private readonly IReadOnlyList<GroupSourceKeyBinding> bindings;
    public ConfiguredGroupSourceKeyProvider(CompositeSecretResolver secrets, IReadOnlyList<GroupSourceKeyBinding> bindings)
    {
        this.secrets = secrets;
        if (bindings is null || bindings.Count > 4096) throw Unavailable();
        var copy = bindings.ToArray();
        foreach (var binding in copy)
        {
            if (binding?.Source is null || binding.Reference is null || string.IsNullOrEmpty(binding.KeyId) ||
                binding.KeyId.Length > 64 || binding.KeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw Unavailable();
            binding.Source.Validate();
        }
        if (copy.Select(x => (x.Source, x.KeyId)).Distinct().Count() != copy.Length ||
            copy.Where(x => x.IsWriteKey).GroupBy(x => x.Source).Any(x => x.Count() != 1)) throw Unavailable();
        this.bindings = Array.AsReadOnly(copy);
    }

    public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default) =>
        ResolveAsync(bindings.SingleOrDefault(x => x.Source == source && x.IsWriteKey), cancellationToken);
    public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default) =>
        ResolveAsync(bindings.SingleOrDefault(x => x.Source == source && string.Equals(x.KeyId, keyId, StringComparison.Ordinal)), cancellationToken);

    private async ValueTask<GroupSourceKeyMaterial> ResolveAsync(GroupSourceKeyBinding? binding, CancellationToken cancellationToken)
    {
        if (binding is null) throw Unavailable();
        byte[]? key = null;
        try
        {
            var value = await secrets.ResolveAsync(binding.Reference, cancellationToken);
            if (value.Length != 44) throw Unavailable();
            key = Convert.FromBase64String(value);
            if (key.Length != 32 || Convert.ToBase64String(key) != value) throw Unavailable();
            return new(binding.KeyId, key);
        }
        catch (Exception error) when (error is FormatException or NotSupportedException or InvalidOperationException)
        { throw Unavailable(); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    private static InvalidOperationException Unavailable() => new("Group source key is unavailable.");
}
