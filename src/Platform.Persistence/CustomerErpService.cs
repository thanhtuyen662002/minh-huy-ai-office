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

/// <summary><see cref="ServerId"/> and <see cref="Database"/> are set when the source uses a shared ERP server.</summary>
public sealed record CustomerErpSource(Guid Id, string LogicalName, bool IsEnabled, string BindingVersion,
    Guid? ServerId = null, string? Database = null);
public sealed class CustomerErpVerificationException(CustomerErpConnectionResult result) : Exception("ERP connection verification failed.")
{
    public CustomerErpConnectionResult Result { get; } = result;
}

public sealed partial class CustomerErpService(PlatformDbContext database, IAuthorizationDirectory directory,
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
        var links = await database.ErpServerSources.AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId)
            .ToDictionaryAsync(row => row.SourceId, token);
        await RequireAdminAsync(authority, token);
        return result.Select(row => Describe(row.Id, row.LogicalName, row.IsEnabled, row.Version, links.GetValueOrDefault(row.Id))).ToArray();
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

    public async Task<CustomerErpReaderResult> CreateReaderAsync(AuthorizationContext authority, CustomerErpConnectionInput admin,
        ICustomerErpReaderProvisioner provisioner, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var result = await provisioner.CreateAsync(admin, token);
        await RequireAdminAsync(authority, token);
        // The audit keeps only a fixed code; neither the administrator nor the new reader credentials are stored.
        await new CustomerErpStorePermissions(database).RequireAsync(token);
        database.CustomerErpProbeAudits.Add(new()
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            Id = Guid.NewGuid(),
            ActorUserId = authority.UserId,
            Code = "reader_" + result.Code,
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
        // Typed connection fields would make a source on a shared server diverge from it; change it through the server.
        return await StoreSourceAsync(authority, request.OperationId, request.LogicalName, sourceId, request.BindingVersion, fingerprint,
            (_, link, _) => link is null ? Task.FromResult(new SourceConnection(connection, null)) : throw new DataSourceRegistrationConflictException(), token);
    }

    /// <summary>The connection a source is saved with, and its shared server link (null for a typed connection).</summary>
    private sealed record SourceConnection(string Connection, ErpServerSourceRecord? Link);

    /// <summary>Creates (<paramref name="sourceId"/> null) or rotates one protected source: verifies the connection
    /// read-only, encrypts it, bumps the binding version and writes the registration audit, all in one transaction.
    /// <paramref name="resolve"/> gets the current secret and server link (both null on create).</summary>
    private async Task<CustomerErpSource> StoreSourceAsync(AuthorizationContext authority, Guid operationId, string? logicalName,
        Guid? sourceId, string? bindingVersion, string fingerprint,
        Func<ProtectedDataSourceSecretRecord?, ErpServerSourceRecord?, CancellationToken, Task<SourceConnection>> resolve, CancellationToken token)
    {
        // Check a completed operation before re-probing: a lost rotation reply
        // can be retried even after the prior password has been retired.
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token);
        try
        {
            await RequireAdminAsync(authority, token);
            await new RegistrationAuditPermissionVerifier(database).RequireAppendOnlyAsync(token);
            await new CustomerErpStorePermissions(database).RequireAsync(token);
            await new ErpServerStorePermissions(database).RequireAsync(token);
            var previous = await database.DataSourceRegistrationAudits.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.OperationId == operationId, token);
            if (previous is not null)
            {
                if (previous.ActorUserId != authority.UserId || previous.RequestHash != fingerprint) throw new DataSourceRegistrationConflictException();
                var saved = await FindSourceAsync(authority, previous.DataSourceId, token) ?? throw new DataSourceRegistrationConflictException();
                var savedSecret = await FindSecretAsync(authority, previous.DataSourceId, token) ?? throw new DataSourceRegistrationConflictException();
                var savedLink = await FindLinkAsync(authority, previous.DataSourceId, token);
                await transaction.CommitAsync(token);
                return Describe(saved, savedSecret.Version, savedLink);
            }
            DataSourceRecord source;
            ProtectedDataSourceSecretRecord secret;
            ErpServerSourceRecord? existingLink = null;
            if (sourceId is { } id)
            {
                source = await FindSourceAsync(authority, id, token) ?? throw DataSourceSecretBindingService.Unavailable();
                secret = await FindSecretAsync(authority, id, token) ?? throw DataSourceSecretBindingService.Unavailable();
                if (!source.AllowRead || source.AllowWrite || source.Kind != "sql-server" || source.ConnectionSecretReference != secret.Reference)
                    throw DataSourceSecretBindingService.Unavailable();
                if (secret.Version.ToString(CultureInfo.InvariantCulture) != bindingVersion || secret.Version == long.MaxValue
                    || (logicalName is not null && source.LogicalName != logicalName)) throw new DataSourceRegistrationConflictException();
                existingLink = await FindLinkAsync(authority, id, token);
                foreach (var entry in database.ChangeTracker.Entries().Where(entry =>
                    entry.Entity is DataSourceRecord existingSource && existingSource.Id == id
                    || entry.Entity is ProtectedDataSourceSecretRecord existingSecret && existingSecret.Id == id
                    || entry.Entity is ErpServerSourceRecord trackedLink && trackedLink.SourceId == id).ToArray())
                    entry.State = EntityState.Detached;
                database.Attach(source); database.Attach(secret);
            }
            else
            {
                if (logicalName is null || await database.DataSources.AnyAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                    && row.LogicalName == logicalName.Trim(), token)) throw new DataSourceRegistrationConflictException();
                var newId = Guid.NewGuid();
                secret = new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, Id = newId };
                source = new()
                {
                    TenantId = authority.TenantId,
                    CompanyId = authority.CompanyId,
                    Id = newId,
                    LogicalName = logicalName.Trim(),
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
            var (connection, link) = await resolve(sourceId is null ? null : secret, existingLink, token);
            var result = await verifier.VerifyAsync(connection, token);
            if (result.Code != "ok") throw new CustomerErpVerificationException(result);
            await RequireAdminAsync(authority, token);
            if (sourceId is null) { database.DataSources.Add(source); database.ProtectedDataSourceSecrets.Add(secret); }
            var encrypted = encryption.Encrypt(authority.TenantId, authority.CompanyId, secret.Id, connection);
            secret.Ciphertext = encrypted.Ciphertext; secret.Nonce = encrypted.Nonce; secret.Tag = encrypted.Tag;
            if (sourceId is not null) secret.Version++;
            secret.UpdatedAtUtc = source.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (link is not null)
            {
                if (existingLink is null)
                {
                    link.TenantId = authority.TenantId; link.CompanyId = authority.CompanyId; link.SourceId = source.Id;
                    database.ErpServerSources.Add(link);
                }
                else
                {
                    database.Attach(existingLink);
                    existingLink.ServerId = link.ServerId; existingLink.Database = link.Database;
                    link = existingLink;
                }
            }
            database.DataSourceRegistrationAudits.Add(new()
            {
                TenantId = authority.TenantId,
                CompanyId = authority.CompanyId,
                Id = Guid.NewGuid(),
                ActorUserId = authority.UserId,
                DataSourceId = source.Id,
                BindingId = secret.Id,
                BindingVersion = secret.Version,
                OperationId = operationId,
                RequestHash = fingerprint,
                OccurredAtUtc = DateTimeOffset.UtcNow
            });
            await RequireAdminAsync(authority, token);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Describe(source, secret.Version, link);
        }
        catch
        {
            // Remove the failed unit of work so a reused scope cannot later save it.
            DetachUnitOfWork();
            throw;
        }
    }

    private void DetachUnitOfWork()
    {
        foreach (var entry in database.ChangeTracker.Entries().Where(entry => entry.Entity is DataSourceRecord
            or ProtectedDataSourceSecretRecord or DataSourceRegistrationAuditRecord
            or ErpServerRecord or ErpServerSourceRecord or ErpServerAuditRecord).ToArray()) entry.State = EntityState.Detached;
    }

    private Task<DataSourceRecord?> FindSourceAsync(AuthorizationContext authority, Guid id, CancellationToken token) =>
        database.DataSources.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.Id == id, token);
    private Task<ProtectedDataSourceSecretRecord?> FindSecretAsync(AuthorizationContext authority, Guid id, CancellationToken token) =>
        database.ProtectedDataSourceSecrets.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.Id == id, token);
    private Task<ErpServerSourceRecord?> FindLinkAsync(AuthorizationContext authority, Guid sourceId, CancellationToken token) =>
        database.ErpServerSources.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.SourceId == sourceId, token);
    private static CustomerErpSource Describe(DataSourceRecord source, long version, ErpServerSourceRecord? link = null) =>
        Describe(source.Id, source.LogicalName, source.IsEnabled, version, link);
    private static CustomerErpSource Describe(Guid id, string logicalName, bool enabled, long version, ErpServerSourceRecord? link) =>
        new(id, logicalName, enabled, version.ToString(CultureInfo.InvariantCulture), link?.ServerId, link?.Database);
}
