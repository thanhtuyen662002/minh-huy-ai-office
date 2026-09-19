using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record DataSourceRegistryWriteRequest(
    string LogicalName,
    string Kind,
    string Environment,
    string Purpose,
    string ConnectionSecretReference,
    bool AllowRead,
    bool AllowWrite,
    int MaxConcurrency,
    bool IsEnabled = true);

public sealed class DataSourceRegistryService(
    PlatformDbContext dbContext,
    IAuthorizationDirectory authorizationDirectory)
{
    public async ValueTask<IReadOnlyList<DataSourceDescriptor>> ListAsync(
        AuthorizationContext authorizationContext,
        CancellationToken cancellationToken = default)
    {
        var authorized = await RequireAuthorizationAsync(
            authorizationContext,
            cancellationToken);

        var records = await dbContext.DataSources
            .AsNoTracking()
            .Where(item =>
                item.TenantId == authorized.Context.TenantId
                && item.CompanyId == authorized.Context.CompanyId)
            .OrderBy(item => item.LogicalName)
            .ToListAsync(cancellationToken);

        return records.Select(ToDescriptor).ToArray();
    }

    public async ValueTask<DataSourceDescriptor> CreateAsync(
        AuthorizationContext authorizationContext,
        DataSourceRegistryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authorized = await RequireAuthorizationAsync(
            authorizationContext,
            cancellationToken);
        var id = Guid.NewGuid();
        var validated = Validate(
            authorized.Context.TenantId,
            authorized.Context.CompanyId,
            id,
            request);
        var secretReference = SecretReference.Parse(request.ConnectionSecretReference);

        if (await LogicalNameExistsAsync(
                authorized.Context,
                validated.LogicalName,
                exceptDataSourceId: null,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "Logical data-source name already exists in the authorized company scope.");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new DataSourceRecord
        {
            TenantId = authorized.Context.TenantId,
            CompanyId = authorized.Context.CompanyId,
            Id = id,
            LogicalName = validated.LogicalName,
            Kind = validated.Kind,
            Environment = validated.Environment,
            Purpose = validated.Purpose,
            ConnectionSecretReference = secretReference.Value,
            AllowRead = validated.AllowRead,
            AllowWrite = validated.AllowWrite,
            MaxConcurrency = validated.MaxConcurrency,
            IsEnabled = validated.IsEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.DataSources.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDescriptor(record);
    }

    public async ValueTask<DataSourceDescriptor?> UpdateAsync(
        AuthorizationContext authorizationContext,
        Guid dataSourceId,
        DataSourceRegistryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (dataSourceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Data source id must be non-empty.",
                nameof(dataSourceId));
        }

        var authorized = await RequireAuthorizationAsync(
            authorizationContext,
            cancellationToken);
        var record = await dbContext.DataSources
            .SingleOrDefaultAsync(
                item =>
                    item.TenantId == authorized.Context.TenantId
                    && item.CompanyId == authorized.Context.CompanyId
                    && item.Id == dataSourceId,
                cancellationToken);

        if (record is null)
        {
            return null;
        }

        var validated = Validate(
            authorized.Context.TenantId,
            authorized.Context.CompanyId,
            dataSourceId,
            request);
        var secretReference = SecretReference.Parse(request.ConnectionSecretReference);

        if (await LogicalNameExistsAsync(
                authorized.Context,
                validated.LogicalName,
                dataSourceId,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "Logical data-source name already exists in the authorized company scope.");
        }

        record.LogicalName = validated.LogicalName;
        record.Kind = validated.Kind;
        record.Environment = validated.Environment;
        record.Purpose = validated.Purpose;
        record.ConnectionSecretReference = secretReference.Value;
        record.AllowRead = validated.AllowRead;
        record.AllowWrite = validated.AllowWrite;
        record.MaxConcurrency = validated.MaxConcurrency;
        record.IsEnabled = validated.IsEnabled;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDescriptor(record);
    }

    private async ValueTask<AuthorizationDirectoryEntry> RequireAuthorizationAsync(
        AuthorizationContext authorizationContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationContext);

        return await authorizationDirectory.ResolveAsync(
                authorizationContext,
                cancellationToken)
            ?? throw new UnauthorizedAccessException(
                "Authorization context is not active for the selected company.");
    }

    private async ValueTask<bool> LogicalNameExistsAsync(
        AuthorizationContext authorizationContext,
        string logicalName,
        Guid? exceptDataSourceId,
        CancellationToken cancellationToken) =>
        await dbContext.DataSources.AnyAsync(
            item =>
                item.TenantId == authorizationContext.TenantId
                && item.CompanyId == authorizationContext.CompanyId
                && item.LogicalName == logicalName
                && (!exceptDataSourceId.HasValue || item.Id != exceptDataSourceId.Value),
            cancellationToken);

    private static DataSourceDescriptor Validate(
        Guid tenantId,
        Guid companyId,
        Guid dataSourceId,
        DataSourceRegistryWriteRequest request) =>
        DataSourceDescriptor.Create(
            tenantId,
            companyId,
            dataSourceId,
            request.LogicalName,
            request.Kind,
            request.Environment,
            request.Purpose,
            request.AllowRead,
            request.AllowWrite,
            request.MaxConcurrency,
            request.IsEnabled);

    private static DataSourceDescriptor ToDescriptor(DataSourceRecord record) =>
        DataSourceDescriptor.Create(
            record.TenantId,
            record.CompanyId,
            record.Id,
            record.LogicalName,
            record.Kind,
            record.Environment,
            record.Purpose,
            record.AllowRead,
            record.AllowWrite,
            record.MaxConcurrency,
            record.IsEnabled);
}
