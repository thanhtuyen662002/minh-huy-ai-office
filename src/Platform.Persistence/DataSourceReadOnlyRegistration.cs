using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DataSourceReadOnlyRegistrationRequest(
    [property: JsonRequired] Guid BindingId,
    [property: JsonRequired] string BindingVersion,
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string LogicalName,
    [property: JsonRequired] string Environment,
    [property: JsonRequired] string Purpose,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int MaxConcurrency);

public sealed class DataSourceRegistrationConflictException() : Exception("Registration conflicts with current state.");

public sealed partial class DataSourceRegistryService
{
    public async Task<DataSourceDescriptor> RegisterReadOnlyAsync(AuthorizationContext authority,
        DataSourceReadOnlyRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.BindingId == Guid.Empty || request.OperationId == Guid.Empty
            || !long.TryParse(request.BindingVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version < 1 || version.ToString(CultureInfo.InvariantCulture) != request.BindingVersion)
            throw new ArgumentException("Invalid registration identity.");
        var validated = DataSourceDescriptor.Create(authority.TenantId, authority.CompanyId, Guid.NewGuid(),
            request.LogicalName, "sql-server", request.Environment, request.Purpose, true, false, request.MaxConcurrency);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            authority.UserId,
            request.BindingId,
            BindingVersion = version,
            validated.LogicalName,
            validated.Environment,
            validated.Purpose,
            validated.MaxConcurrency
        }))));

        // Serializable keeps directory/grant and idempotency range locks through
        // the source+audit commit. A concurrent revoker must serialize around it.
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(dbContext, cancellationToken);
        DataSourceRecord? source = null;
        DataSourceRegistrationAuditRecord? audit = null;
        try
        {
            var authorized = await RequireManagementAuthorizationAsync(authority, cancellationToken);
            if (authorized.Context != authority) throw DataSourceSecretBindingService.Unavailable();
            var grant = await bindings.RequireBindingAsync(authority, request.BindingId, version, cancellationToken);
            await new RegistrationAuditPermissionVerifier(dbContext).RequireAppendOnlyAsync(cancellationToken);

            var previous = await dbContext.DataSourceRegistrationAudits.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                && row.OperationId == request.OperationId, cancellationToken);
            if (previous is not null)
            {
                if (previous.ActorUserId != authority.UserId || previous.RequestHash != fingerprint)
                    throw new DataSourceRegistrationConflictException();
                var saved = await dbContext.DataSources.AsNoTracking().SingleOrDefaultAsync(row =>
                    row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                    && row.Id == previous.DataSourceId, cancellationToken);
                if (saved is null) throw new DataSourceRegistrationConflictException();
                await transaction.CommitAsync(cancellationToken);
                return ToDescriptor(saved);
            }
            if (await LogicalNameExistsAsync(authority, validated.LogicalName, null, cancellationToken))
                throw new DataSourceRegistrationConflictException();
            var now = DateTimeOffset.UtcNow;
            source = new DataSourceRecord
            {
                TenantId = authority.TenantId,
                CompanyId = authority.CompanyId,
                Id = validated.Id,
                LogicalName = validated.LogicalName,
                Kind = "sql-server",
                Environment = validated.Environment,
                Purpose = validated.Purpose,
                ConnectionSecretReference = grant.CanonicalReference,
                AllowRead = true,
                AllowWrite = false,
                MaxConcurrency = validated.MaxConcurrency,
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            audit = new DataSourceRegistrationAuditRecord
            {
                TenantId = authority.TenantId,
                CompanyId = authority.CompanyId,
                Id = Guid.NewGuid(),
                ActorUserId = authority.UserId,
                DataSourceId = source.Id,
                BindingId = grant.Id,
                BindingVersion = grant.Version,
                OperationId = request.OperationId,
                RequestHash = fingerprint,
                OccurredAtUtc = now
            };
            dbContext.DataSources.Add(source);
            dbContext.DataSourceRegistrationAudits.Add(audit);
            // Fresh validation also protects synthetic/nonrelational tests and
            // any future directory implementation which does not use this DB.
            var current = await RequireManagementAuthorizationAsync(authority, cancellationToken);
            if (current.Context != authority) throw DataSourceSecretBindingService.Unavailable();
            await bindings.RequireBindingAsync(authority, grant.Id, grant.Version, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToDescriptor(source);
        }
        catch
        {
            // A failed Save/commit must not leave retryable added entities in a
            // reused context. SQL rollback is guaranteed by transaction disposal.
            if (source is not null) dbContext.Entry(source).State = EntityState.Detached;
            if (audit is not null) dbContext.Entry(audit).State = EntityState.Detached;
            throw;
        }
    }
}
