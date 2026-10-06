using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>Only boundary allowed to resolve a customer data-source credential.</summary>
public sealed class ScopedDataSourceSecretResolver(
    DataSourceSecretBindingService bindings, CompositeSecretResolver secrets)
{
    public async Task<T> UseAsync<T>(AuthorizationContext authority, Guid sourceId, bool readOnly,
        Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default, Guid? taskId = null)
    {
        await RequireTaskOwnerAsync(authority, taskId, cancellationToken);
        var binding = await bindings.RequireSourceAsync(authority, sourceId, readOnly, cancellationToken);
        var value = await secrets.ResolveAsync(binding.Reference, cancellationToken);
        // Revocation/rotation while the provider awaited cannot authorize the use.
        await RequireTaskOwnerAsync(authority, taskId, cancellationToken);
        await bindings.RevalidateAsync(binding, readOnly, cancellationToken);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Data source connection is unavailable.");
        return await use(value, cancellationToken);
    }

    private async Task RequireTaskOwnerAsync(AuthorizationContext authority, Guid? taskId, CancellationToken cancellationToken)
    {
        if (taskId is not { } id) return;
        var current = await bindings.TaskAuthorityAsync(authority.TenantId, authority.CompanyId, id, cancellationToken);
        if (current.UserId != authority.UserId) throw DataSourceSecretBindingService.Unavailable();
    }
}
