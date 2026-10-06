using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public interface IDataSourceConnectionProbe
{
    ValueTask ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default);
}

public sealed class SqlDataSourceConnectionProbe(
    ISqlConnectionFactory connectionFactory) : IDataSourceConnectionProbe
{
    public async ValueTask ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
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
            return await scopedSecrets.UseAsync(authorized.Context, dataSourceId, readOnly: false,
                async (connectionString, token) =>
                {
                    try { await connectionProbe.ProbeAsync(connectionString, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
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
