using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record AuthorizedDataSourceBinding(
    AuthorizationContext Authority, Guid SourceId, SecretReference Reference, Guid GrantId, long GrantVersion);

public sealed record DataSourceRegistrationOption(Guid BindingId, string Label, long Version);
public sealed record DataSourceRegistrationOptionsPage(IReadOnlyList<DataSourceRegistrationOption> Items, int Offset, int Limit, bool HasMore);

public sealed class DataSourceSecretBindingService(
    PlatformDbContext database,
    IAuthorizationDirectory directory,
    BindingStorePermissionVerifier? permissions = null,
    string? infrastructureReference = null)
{
    private readonly BindingStorePermissionVerifier verifier = permissions ?? new(database);

    public static UnauthorizedAccessException Unavailable() => new("Data source is unavailable for authorized use.");
    public Task RequireStoreAsync(CancellationToken cancellationToken = default) => verifier.RequireReadOnlyAsync(cancellationToken);

    public async Task<DataSourceRegistrationOptionsPage> ListRegistrationOptionsAsync(
        AuthorizationContext authority, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (offset is < 0 or > 1000 || limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(offset), "Invalid registration options page.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await RequireAdministrationAsync(authority, cancellationToken);
            await verifier.RequireReadOnlyAsync(cancellationToken);
            // Bound the company snapshot before parsing references. Filtering
            // before paging makes HasMore describe actual safe choices.
            var candidates = await database.DataSourceSecretBindings.AsNoTracking()
                .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                    && row.IsEnabled && row.Version > 0 && row.Id != Guid.Empty)
                .OrderBy(row => row.Id).Take(1001).ToArrayAsync(cancellationToken);
            if (candidates.Length > 1000) throw Unavailable();
            var options = candidates.Where(row =>
                    !string.IsNullOrWhiteSpace(row.Label) && row.Label.Length <= 128
                    && !row.Label.Contains("secretref://", StringComparison.OrdinalIgnoreCase)
                    && SecretReference.TryParse(row.CanonicalReference, out var reference)
                    && string.Equals(reference!.Value, row.CanonicalReference, StringComparison.Ordinal)
                    && !IsInfrastructureReference(reference))
                .Select(row => new DataSourceRegistrationOption(row.Id, row.Label.Trim(), row.Version))
                .Skip(offset).Take(limit + 1).ToArray();
            // A query can span a role revocation. Re-resolve before releasing metadata.
            await RequireAdministrationAsync(authority, cancellationToken);
            return new(options.Take(limit).ToArray(), offset, limit, options.Length > limit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            throw Unavailable();
        }
    }

    private async Task RequireAdministrationAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        var current = await directory.ResolveAsync(authority, cancellationToken);
        if (current is null || current.Context != authority || !current.Roles.Contains("admin", StringComparer.Ordinal))
            throw Unavailable();
    }

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
