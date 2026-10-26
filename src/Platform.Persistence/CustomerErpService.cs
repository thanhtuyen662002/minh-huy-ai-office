using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CustomerErpSaveRequest : CustomerErpConnectionInput
{
    public Guid OperationId { get; init; }
    public string LogicalName { get; init; } = "";
    public string? BindingVersion { get; init; }
}

public sealed record CustomerErpSource(Guid Id, string LogicalName, bool IsEnabled, string BindingVersion);
public sealed class CustomerErpVerificationException(CustomerErpConnectionResult result) : Exception("ERP connection verification failed.")
{
    public CustomerErpConnectionResult Result { get; } = result;
}

public sealed class CustomerErpService(PlatformDbContext database, IAuthorizationDirectory directory,
    ProtectedSecretEncryption encryption, ICustomerErpConnectionVerifier verifier)
{
    private async Task RequireAdminAsync(AuthorizationContext authority, CancellationToken token)
    {
        var current = await directory.ResolveAsync(authority, token);
        if (current is null || current.Context != authority || !current.Roles.Contains("admin", StringComparer.Ordinal))
            throw DataSourceSecretBindingService.Unavailable();
        await new BindingStorePermissionVerifier(database).RequireReadOnlyAsync(token);
    }

    public async Task<IReadOnlyList<CustomerErpSource>> ListAsync(AuthorizationContext authority, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var result = await (from source in database.DataSources.AsNoTracking()
                            join secret in database.ProtectedDataSourceSecrets.AsNoTracking()
                                on new { source.TenantId, source.CompanyId, source.Id } equals new { secret.TenantId, secret.CompanyId, secret.Id }
                            where source.TenantId == authority.TenantId && source.CompanyId == authority.CompanyId
                            orderby source.LogicalName
                            select new { source.Id, source.LogicalName, source.IsEnabled, secret.Version }).Take(1000).ToArrayAsync(token);
        await RequireAdminAsync(authority, token);
        return result.Select(row => new CustomerErpSource(row.Id, row.LogicalName, row.IsEnabled, row.Version.ToString(CultureInfo.InvariantCulture))).ToArray();
    }

    public async Task<CustomerErpConnectionResult> TestAsync(AuthorizationContext authority, CustomerErpConnectionInput request, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var result = await verifier.VerifyAsync(request.BuildConnectionString(), token);
        await RequireAdminAsync(authority, token);
        // Append-only audit contains only a fixed result code and actor/scope.
        await new CustomerErpStorePermissions(database).RequireAsync(token);
        database.CustomerErpProbeAudits.Add(new()
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            Id = Guid.NewGuid(),
            ActorUserId = authority.UserId,
            Code = result.Code,
            OccurredAtUtc = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync(token);
        return result;
    }

    public async Task<CustomerErpSource> SaveAsync(AuthorizationContext authority, CustomerErpSaveRequest request,
        Guid? sourceId = null, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var connection = request.BuildConnectionString();
        if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.LogicalName)
            || request.LogicalName.Length > 200 || request.LogicalName.Any(char.IsControl)
            || (sourceId is null && request.BindingVersion is not null)
            || (sourceId is not null && (!long.TryParse(request.BindingVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
                || v < 1 || v.ToString(CultureInfo.InvariantCulture) != request.BindingVersion)))
            throw new ArgumentException("Invalid ERP save request.");
        var fingerprint = encryption.Fingerprint(JsonSerializer.Serialize(new { authority.UserId, SourceId = sourceId, Request = request }));
        // Check a completed operation before re-probing: a lost rotation reply
        // can be retried even after the prior password has been retired.
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token);
        try
        {
            await RequireAdminAsync(authority, token);
            await new RegistrationAuditPermissionVerifier(database).RequireAppendOnlyAsync(token);
            await new CustomerErpStorePermissions(database).RequireAsync(token);
            var previous = await database.DataSourceRegistrationAudits.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.OperationId == request.OperationId, token);
            if (previous is not null)
            {
                if (previous.ActorUserId != authority.UserId || previous.RequestHash != fingerprint) throw new DataSourceRegistrationConflictException();
                var saved = await FindSourceAsync(authority, previous.DataSourceId, token) ?? throw new DataSourceRegistrationConflictException();
                var savedSecret = await FindSecretAsync(authority, previous.DataSourceId, token) ?? throw new DataSourceRegistrationConflictException();
                await transaction.CommitAsync(token);
                return Describe(saved, savedSecret.Version);
            }
            DataSourceRecord source;
            ProtectedDataSourceSecretRecord secret;
            if (sourceId is { } id)
            {
                source = await FindSourceAsync(authority, id, token) ?? throw DataSourceSecretBindingService.Unavailable();
                secret = await FindSecretAsync(authority, id, token) ?? throw DataSourceSecretBindingService.Unavailable();
                if (!source.AllowRead || source.AllowWrite || source.Kind != "sql-server" || source.ConnectionSecretReference != secret.Reference)
                    throw DataSourceSecretBindingService.Unavailable();
                if (secret.Version.ToString(CultureInfo.InvariantCulture) != request.BindingVersion || secret.Version == long.MaxValue
                    || source.LogicalName != request.LogicalName) throw new DataSourceRegistrationConflictException();
                foreach (var entry in database.ChangeTracker.Entries().Where(entry =>
                    entry.Entity is DataSourceRecord existingSource && existingSource.Id == id
                    || entry.Entity is ProtectedDataSourceSecretRecord existingSecret && existingSecret.Id == id).ToArray())
                    entry.State = EntityState.Detached;
                database.Attach(source); database.Attach(secret);
            }
            else
            {
                if (await database.DataSources.AnyAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                    && row.LogicalName == request.LogicalName.Trim(), token)) throw new DataSourceRegistrationConflictException();
                var newId = Guid.NewGuid();
                secret = new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, Id = newId };
                source = new()
                {
                    TenantId = authority.TenantId,
                    CompanyId = authority.CompanyId,
                    Id = newId,
                    LogicalName = request.LogicalName.Trim(),
                    Kind = "sql-server",
                    Environment = "Production",
                    Purpose = "ERP khách hàng",
                    ConnectionSecretReference = secret.Reference,
                    AllowRead = true,
                    AllowWrite = false,
                    IsEnabled = true,
                    MaxConcurrency = 2,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
            }
            var result = await verifier.VerifyAsync(connection, token);
            if (result.Code != "ok") throw new CustomerErpVerificationException(result);
            await RequireAdminAsync(authority, token);
            if (sourceId is null) { database.DataSources.Add(source); database.ProtectedDataSourceSecrets.Add(secret); }
            var encrypted = encryption.Encrypt(authority.TenantId, authority.CompanyId, secret.Id, connection);
            secret.Ciphertext = encrypted.Ciphertext; secret.Nonce = encrypted.Nonce; secret.Tag = encrypted.Tag;
            if (sourceId is not null) secret.Version++;
            secret.UpdatedAtUtc = source.UpdatedAtUtc = DateTimeOffset.UtcNow;
            database.DataSourceRegistrationAudits.Add(new()
            {
                TenantId = authority.TenantId,
                CompanyId = authority.CompanyId,
                Id = Guid.NewGuid(),
                ActorUserId = authority.UserId,
                DataSourceId = source.Id,
                BindingId = secret.Id,
                BindingVersion = secret.Version,
                OperationId = request.OperationId,
                RequestHash = fingerprint,
                OccurredAtUtc = DateTimeOffset.UtcNow
            });
            await RequireAdminAsync(authority, token);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Describe(source, secret.Version);
        }
        catch
        {
            // Remove the failed unit of work so a reused scope cannot later save it.
            foreach (var entry in database.ChangeTracker.Entries().Where(entry => entry.Entity is DataSourceRecord
                or ProtectedDataSourceSecretRecord or DataSourceRegistrationAuditRecord).ToArray()) entry.State = EntityState.Detached;
            throw;
        }
    }

    private Task<DataSourceRecord?> FindSourceAsync(AuthorizationContext authority, Guid id, CancellationToken token) =>
        database.DataSources.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.Id == id, token);
    private Task<ProtectedDataSourceSecretRecord?> FindSecretAsync(AuthorizationContext authority, Guid id, CancellationToken token) =>
        database.ProtectedDataSourceSecrets.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.Id == id, token);
    private static CustomerErpSource Describe(DataSourceRecord source, long version) => new(source.Id, source.LogicalName, source.IsEnabled, version.ToString(CultureInfo.InvariantCulture));
}
