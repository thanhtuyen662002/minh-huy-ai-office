using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record AuthorizedDataSourceBinding(
    AuthorizationContext Authority, Guid SourceId, SecretReference Reference, Guid GrantId, long GrantVersion);

public sealed class DataSourceSecretBindingService(
    PlatformDbContext database,
    IAuthorizationDirectory directory,
    BindingStorePermissionVerifier? permissions = null,
    string? infrastructureReference = null)
{
    private readonly BindingStorePermissionVerifier verifier = permissions ?? new(database);

    public static UnauthorizedAccessException Unavailable() => new("Data source is unavailable for authorized use.");
    public Task RequireStoreAsync(CancellationToken cancellationToken = default) => verifier.RequireReadOnlyAsync(cancellationToken);

    public async Task<DataSourceSecretBindingRecord> RequireReferenceAsync(
        AuthorizationContext authority, string? reference, CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await directory.ResolveAsync(authority, cancellationToken);
            if (current is null || current.Context != authority) throw Unavailable();
            await verifier.RequireReadOnlyAsync(cancellationToken);
            if (!SecretReference.TryParse(reference, out var parsed) || IsInfrastructureReference(parsed!))
                throw Unavailable();
            var grants = await database.Set<DataSourceSecretBindingRecord>().AsNoTracking()
                .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                    && row.CanonicalReference == parsed!.Value)
                .Take(2).ToArrayAsync(cancellationToken);
            if (grants.Length != 1 || !grants[0].IsEnabled || grants[0].Version < 1
                || !string.Equals(grants[0].CanonicalReference, parsed!.Value, StringComparison.Ordinal))
                throw Unavailable();
            return grants[0];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            throw Unavailable();
        }
    }

    public async Task<AuthorizedDataSourceBinding> RequireSourceAsync(
        AuthorizationContext authority, Guid sourceId, bool readOnly, CancellationToken cancellationToken = default)
    {
        try
        {
            // Re-load the source: a tracked entity/permission result is not current authority.
            var source = await database.DataSources.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.Id == sourceId,
                cancellationToken);
            if (source is null || !source.IsEnabled || (!source.AllowRead && !source.AllowWrite)
                || (readOnly && (!source.AllowRead || source.AllowWrite))
                || source.MaxConcurrency is < 1 or > 1024) throw Unavailable();
            var grant = await RequireReferenceAsync(authority, source.ConnectionSecretReference, cancellationToken);
            return new(authority, sourceId, SecretReference.Parse(grant.CanonicalReference), grant.Id, grant.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            throw Unavailable();
        }
    }

    public async Task<AuthorizationContext> TaskAuthorityAsync(
        Guid tenantId, Guid companyId, Guid taskId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await database.Tasks.AsNoTracking().Where(row =>
                row.TenantId == tenantId && row.CompanyId == companyId && row.Id == taskId)
                .Select(row => row.CreatedByUserId).SingleOrDefaultAsync(cancellationToken);
            if (user == Guid.Empty) throw Unavailable();
            return AuthorizationContext.Create(tenantId, companyId, user);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            throw Unavailable();
        }
    }

    public async Task RevalidateAsync(AuthorizedDataSourceBinding binding, bool readOnly,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireSourceAsync(binding.Authority, binding.SourceId, readOnly, cancellationToken);
        if (current.GrantId != binding.GrantId || current.GrantVersion != binding.GrantVersion
            || !string.Equals(current.Reference.Value, binding.Reference.Value, StringComparison.Ordinal))
            throw Unavailable();
    }

    private bool IsInfrastructureReference(SecretReference reference)
    {
        // Windows env keys ignore case. Compare canonical references, because the
        // resolver receives the canonical grant, including normalized path aliases.
        // Customer grant lookup and version revalidation remain strictly ordinal.
        var comparison = reference.Provider == "env" && OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (reference.Provider == "env" && string.Equals(reference.Value,
                "secretref://env/AIOFFICE_DB_CONNECTION", comparison))
            || (SecretReference.TryParse(infrastructureReference, out var platform)
                && string.Equals(reference.Provider, platform!.Provider, StringComparison.Ordinal)
                && string.Equals(reference.Value, platform.Value, comparison));
    }
}
