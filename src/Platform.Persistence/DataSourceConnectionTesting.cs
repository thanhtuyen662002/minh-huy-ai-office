using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public interface IDataSourceConnectionProbe
{
    ValueTask ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    ValueTask ProbeAsync(string connectionString, bool requireReadOnly,
        CancellationToken cancellationToken = default)
    {
        // Legacy implementations cannot silently claim credential qualification.
        if (requireReadOnly) throw ErpReadOnlyConnectionVerifier.Unqualified();
        return ProbeAsync(connectionString, cancellationToken);
    }
}

public sealed class SqlDataSourceConnectionProbe(
    ISqlConnectionFactory connectionFactory) : IDataSourceConnectionProbe
{
    public async ValueTask ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
        => await ProbeAsync(connectionString, requireReadOnly: false, cancellationToken);

    public async ValueTask ProbeAsync(string connectionString, bool requireReadOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (requireReadOnly) await ErpReadOnlyConnectionVerifier.RequireAsync(connection, cancellationToken);
    }
}

public sealed class DataSourceConnectionTestService(
    PlatformDbContext dbContext,
    IAuthorizationDirectory authorizationDirectory,
    CompositeSecretResolver secretResolver,
    IDataSourceConnectionProbe connectionProbe,
    DataSourceSecretBindingService? bindingService = null)
{
    private readonly ScopedDataSourceSecretResolver scopedSecrets = new(
        bindingService ?? new(dbContext, authorizationDirectory), secretResolver);
    public async ValueTask<DataSourceConnectionTestResult> TestAsync(
        AuthorizationContext authorizationContext,
        Guid dataSourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationContext);

        if (dataSourceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Data source id must be non-empty.",
                nameof(dataSourceId));
        }

        var authorized = await authorizationDirectory.ResolveAsync(
            authorizationContext,
            cancellationToken);

        if (authorized is null)
        {
            return DataSourceConnectionTestResult.NotAuthorized();
        }

        var dataSource = await dbContext.DataSources
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.TenantId == authorized.Context.TenantId
                    && item.CompanyId == authorized.Context.CompanyId
                    && item.Id == dataSourceId,
                cancellationToken);

        if (dataSource is null)
        {
            return DataSourceConnectionTestResult.NotFound();
        }

        if (!dataSource.IsEnabled)
        {
            return DataSourceConnectionTestResult.Disabled();
        }

        if ((!dataSource.AllowRead && !dataSource.AllowWrite)
            || dataSource.MaxConcurrency is < 1 or > 1024
            || !SecretReference.TryParse(
                dataSource.ConnectionSecretReference,
                out var secretReference))
        {
            return DataSourceConnectionTestResult.InvalidConfiguration();
        }

        try
        {
            var requireReadOnly = dataSource.AllowRead && !dataSource.AllowWrite;
            return await scopedSecrets.UseAsync(authorized.Context, dataSourceId, readOnly: requireReadOnly,
                async (connectionString, token) =>
                {
                    // Source policy can change while secret resolution or probing awaits.
                    // Never downgrade a newly read-only source to connectivity-only success.
                    async Task<bool> PolicyStillMatchesAsync()
                    {
                        var current = await dbContext.DataSources.AsNoTracking().SingleOrDefaultAsync(row =>
                            row.TenantId == authorized.Context.TenantId && row.CompanyId == authorized.Context.CompanyId
                            && row.Id == dataSourceId, token);
                        return current is not null && current.IsEnabled && current.AllowRead == dataSource.AllowRead
                            && current.AllowWrite == dataSource.AllowWrite
                            && current.MaxConcurrency == dataSource.MaxConcurrency
                            && string.Equals(current.ConnectionSecretReference, dataSource.ConnectionSecretReference, StringComparison.Ordinal);
                    }
                    try
                    {
                        if (!await PolicyStillMatchesAsync()) return DataSourceConnectionTestResult.NotAuthorized();
                        await connectionProbe.ProbeAsync(connectionString, requireReadOnly, token);
                        if (!await PolicyStillMatchesAsync()) return DataSourceConnectionTestResult.NotAuthorized();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (ErpReadOnlyCredentialsException) { return DataSourceConnectionTestResult.ReadOnlyUnqualified(); }
                    catch { return DataSourceConnectionTestResult.ConnectionFailed(); }
                    return DataSourceConnectionTestResult.Success();
                }, cancellationToken);
        }
        catch (UnauthorizedAccessException) { return DataSourceConnectionTestResult.NotAuthorized(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DataSourceConnectionTestResult.SecretUnavailable();
        }

    }
}
