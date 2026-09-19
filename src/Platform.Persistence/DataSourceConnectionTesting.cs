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
    CompositeSecretResolver secretResolver,
    IDataSourceConnectionProbe connectionProbe)
{
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

        var dataSource = await dbContext.DataSources
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.TenantId == authorizationContext.TenantId
                    && item.CompanyId == authorizationContext.CompanyId
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

        string connectionString;
        try
        {
            connectionString = await secretResolver.ResolveAsync(
                secretReference!,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DataSourceConnectionTestResult.SecretUnavailable();
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return DataSourceConnectionTestResult.SecretUnavailable();
        }

        try
        {
            await connectionProbe.ProbeAsync(connectionString, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DataSourceConnectionTestResult.ConnectionFailed();
        }

        return DataSourceConnectionTestResult.Success();
    }
}
