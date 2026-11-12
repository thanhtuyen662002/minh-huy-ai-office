using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>Shared ERP servers: one address and read-only login for several customer databases. Every linked source keeps
/// its own protected connection string (what readers use), rebuilt from the server whenever the server changes.</summary>
public sealed partial class CustomerErpService
{
    private const int MaximumServers = 200;

    public async Task<IReadOnlyList<ErpServer>> ListServersAsync(AuthorizationContext authority, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var servers = await database.ErpServers.AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId)
            .OrderBy(row => row.Name).Take(MaximumServers).ToArrayAsync(token);
        var counts = await database.ErpServerSources.AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId)
            .GroupBy(row => row.ServerId).Select(group => new { group.Key, Count = group.Count() }).ToDictionaryAsync(row => row.Key, row => row.Count, token);
        await RequireAdminAsync(authority, token);
        return servers.Select(server => DescribeServer(server, ReadServer(server), counts.GetValueOrDefault(server.Id))).ToArray();
    }

    /// <summary>Creates a server (<paramref name="serverId"/> null) or changes one. A change re-verifies every linked database
    /// with the new address/login and rewrites all of their connections in the same transaction; if any database fails,
    /// nothing is saved and the failing source is named.</summary>
    public async Task<ErpServer> SaveServerAsync(AuthorizationContext authority, ErpServerSaveRequest request,
        Guid? serverId = null, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var name = request.Name.Trim();
        if (request.OperationId == Guid.Empty || name.Length is 0 or > 200 || name.Any(char.IsControl)
            || (serverId is null) != (request.Version is null) || (serverId is not null && !IsVersion(request.Version))
            || (serverId is null && (request.UserName.Length == 0 || request.Password.Length == 0)))
            throw new ArgumentException("Invalid ERP server request.");
        var fingerprint = encryption.Fingerprint(JsonSerializer.Serialize(new { authority.UserId, ServerId = serverId, Request = request }));
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token);
        try
        {
            await RequireAdminAsync(authority, token);
            await new CustomerErpStorePermissions(database).RequireAsync(token);
            await new ErpServerStorePermissions(database).RequireAsync(token);
            var previous = await database.ErpServerAudits.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.OperationId == request.OperationId, token);
            if (previous is not null)
            {
                if (previous.ActorUserId != authority.UserId || previous.RequestHash != fingerprint) throw new DataSourceRegistrationConflictException();
                var saved = await FindServerAsync(authority, previous.ServerId, token) ?? throw new DataSourceRegistrationConflictException();
                var linked = await database.ErpServerSources.CountAsync(row => row.TenantId == authority.TenantId
                    && row.CompanyId == authority.CompanyId && row.ServerId == saved.Id, token);
                await transaction.CommitAsync(token);
                return DescribeServer(saved, ReadServer(saved), linked);
            }
            ErpServerRecord server;
            ErpServerConnection? current = null;
            if (serverId is { } id)
            {
                server = await FindServerAsync(authority, id, token) ?? throw DataSourceSecretBindingService.Unavailable();
                if (server.Version.ToString(CultureInfo.InvariantCulture) != request.Version || server.Version == long.MaxValue)
                    throw new DataSourceRegistrationConflictException();
                current = ReadServer(server);
                foreach (var entry in database.ChangeTracker.Entries().Where(entry => entry.Entity is ErpServerRecord tracked && tracked.Id == id).ToArray())
                    entry.State = EntityState.Detached;
                database.Attach(server);
            }
            else
            {
                if (await database.ErpServers.CountAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId, token) >= MaximumServers)
                    throw new DataSourceRegistrationConflictException();
                server = new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, Id = Guid.NewGuid() };
            }
            if (await database.ErpServers.AnyAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                && row.Name == name && row.Id != server.Id, token)) throw new DataSourceRegistrationConflictException();
            var connection = new ErpServerConnection
            {
                Host = request.Host,
                Port = request.Port,
                UserName = request.UserName.Length > 0 ? request.UserName : current?.UserName ?? "",
                Password = request.Password.Length > 0 ? request.Password : current?.Password ?? "",
                Encrypt = request.Encrypt,
                TrustServerCertificate = request.TrustServerCertificate
            };
            // Field validation only; "master" stands in for the per-source database.
            _ = connection.For("master").BuildConnectionString();

            // A new server has no sources yet.
            var linkedSources = await (
                from link in database.ErpServerSources.AsNoTracking()
                join source in database.DataSources.AsNoTracking()
                    on new { link.TenantId, link.CompanyId, Id = link.SourceId } equals new { source.TenantId, source.CompanyId, source.Id }
                join secret in database.ProtectedDataSourceSecrets.AsNoTracking()
                    on new { link.TenantId, link.CompanyId, Id = link.SourceId } equals new { secret.TenantId, secret.CompanyId, secret.Id }
                where link.TenantId == authority.TenantId && link.CompanyId == authority.CompanyId && link.ServerId == server.Id
                orderby source.LogicalName
                select new { link, source, secret }).ToArrayAsync(token);
            foreach (var row in linkedSources)
                if (!row.source.AllowRead || row.source.AllowWrite || row.source.Kind != "sql-server"
                    || row.source.ConnectionSecretReference != row.secret.Reference || row.secret.Version == long.MaxValue)
                    throw DataSourceSecretBindingService.Unavailable();
            var connections = linkedSources.Select(row => connection.For(row.link.Database).BuildConnectionString()).ToArray();
            // Every database is checked with the new login before any of them is changed.
            var results = await Task.WhenAll(connections.Select(value => verifier.VerifyAsync(value, token)));
            for (var index = 0; index < results.Length; index++)
                if (results[index].Code != "ok") throw new ErpServerVerificationException(results[index], linkedSources[index].source.LogicalName);
            await RequireAdminAsync(authority, token);

            var now = DateTimeOffset.UtcNow;
            server.Name = name;
            var payload = encryption.Encrypt(authority.TenantId, authority.CompanyId, server.Id, JsonSerializer.Serialize(connection),
                ProtectedSecretEncryption.ErpServerPurpose);
            server.Ciphertext = payload.Ciphertext; server.Nonce = payload.Nonce; server.Tag = payload.Tag;
            server.UpdatedAtUtc = now;
            if (serverId is null) database.ErpServers.Add(server);
            else server.Version++;
            for (var index = 0; index < linkedSources.Length; index++)
            {
                var secret = linkedSources[index].secret;
                foreach (var entry in database.ChangeTracker.Entries().Where(entry => entry.Entity is ProtectedDataSourceSecretRecord tracked && tracked.Id == secret.Id).ToArray())
                    entry.State = EntityState.Detached;
                database.Attach(secret);
                var encrypted = encryption.Encrypt(authority.TenantId, authority.CompanyId, secret.Id, connections[index]);
                secret.Ciphertext = encrypted.Ciphertext; secret.Nonce = encrypted.Nonce; secret.Tag = encrypted.Tag;
                // A new binding version retires reader snapshots taken with the old login.
                secret.Version++;
                secret.UpdatedAtUtc = now;
            }
            database.ErpServerAudits.Add(new()
            {
                TenantId = authority.TenantId,
                CompanyId = authority.CompanyId,
                Id = Guid.NewGuid(),
                ActorUserId = authority.UserId,
                ServerId = server.Id,
                ServerVersion = server.Version,
                SourceCount = linkedSources.Length,
                OperationId = request.OperationId,
                RequestHash = fingerprint,
                OccurredAtUtc = now
            });
            await RequireAdminAsync(authority, token);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return DescribeServer(server, connection, linkedSources.Length);
        }
        catch
        {
            DetachUnitOfWork();
            throw;
        }
    }

    /// <summary>Creates a source on a shared server (<paramref name="sourceId"/> null, needs a name and database) or moves an
    /// existing source onto one or to another database (needs its binding version; a blank database keeps the current one).</summary>
    public async Task<CustomerErpSource> SaveSourceOnServerAsync(AuthorizationContext authority, ErpServerSourceRequest request,
        Guid? sourceId = null, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var databaseName = request.Database.Trim();
        if (request.OperationId == Guid.Empty || request.ServerId == Guid.Empty || databaseName.Length > 128 || databaseName.Any(char.IsControl)
            || (sourceId is null && (databaseName.Length == 0 || request.BindingVersion is not null || string.IsNullOrWhiteSpace(request.LogicalName)
                || request.LogicalName.Length > 200 || request.LogicalName.Any(char.IsControl)))
            || (sourceId is not null && (request.LogicalName is not null || !IsVersion(request.BindingVersion))))
            throw new ArgumentException("Invalid ERP source request.");
        var fingerprint = encryption.Fingerprint(JsonSerializer.Serialize(new { authority.UserId, SourceId = sourceId, Server = request }));
        return await StoreSourceAsync(authority, request.OperationId, request.LogicalName, sourceId, request.BindingVersion, fingerprint,
            async (secret, link, token) =>
            {
                var server = await FindServerAsync(authority, request.ServerId, token) ?? throw DataSourceSecretBindingService.Unavailable();
                var target = databaseName.Length > 0 ? databaseName : link?.Database ?? CurrentDatabase(authority, secret!);
                return new SourceConnection(ReadServer(server).For(target).BuildConnectionString(),
                    new ErpServerSourceRecord { ServerId = server.Id, Database = target });
            }, token);
    }

    private string CurrentDatabase(AuthorizationContext authority, ProtectedDataSourceSecretRecord secret)
    {
        var name = new SqlConnectionStringBuilder(encryption.Decrypt(authority.TenantId, authority.CompanyId, secret.Id,
            new(secret.Ciphertext, secret.Nonce, secret.Tag))).InitialCatalog;
        return string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("The source has no database.") : name;
    }

    private Task<ErpServerRecord?> FindServerAsync(AuthorizationContext authority, Guid id, CancellationToken token) =>
        database.ErpServers.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.Id == id, token);

    private ErpServerConnection ReadServer(ErpServerRecord server) =>
        JsonSerializer.Deserialize<ErpServerConnection>(encryption.Decrypt(server.TenantId, server.CompanyId, server.Id,
            new(server.Ciphertext, server.Nonce, server.Tag), ProtectedSecretEncryption.ErpServerPurpose))
        ?? throw DataSourceSecretBindingService.Unavailable();

    private static ErpServer DescribeServer(ErpServerRecord server, ErpServerConnection connection, int sources) =>
        new(server.Id, server.Name, connection.Host, connection.Port, connection.UserName, connection.Encrypt,
            connection.TrustServerCertificate, server.Version.ToString(CultureInfo.InvariantCulture), sources);

    private static bool IsVersion(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 1
        && version.ToString(CultureInfo.InvariantCulture) == value;
}
