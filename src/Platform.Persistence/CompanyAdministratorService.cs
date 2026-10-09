using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompanyAdministratorRequest(
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] bool IsAdministrator);

public sealed record CompanyAdministratorResult(Guid CompanyId, Guid UserId, bool IsAdministrator,
    string MembershipVersion, Guid OperationId);

public sealed class CompanyAdministratorService(PlatformDbContext database, IAuthorizationDirectory directory)
{
    public async Task<CompanyAdministratorResult> SetAdministratorAsync(AuthorizationContext authority, Guid userId,
        CompanyAdministratorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (userId == Guid.Empty || request.OperationId == Guid.Empty ||
            !long.TryParse(request.ExpectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) ||
            expected < 1 || expected.ToString(CultureInfo.InvariantCulture) != request.ExpectedVersion)
            throw new ArgumentException("Invalid administrator request.");
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Company administration is unavailable.");
        await RequireAdminAsync(authority, cancellationToken);
        if (userId == authority.UserId) throw new CompanyMembershipAccessConflictException("self-role-change");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Operation = "company-administrator",
            authority.UserId,
            TargetUserId = userId,
            ExpectedVersion = expected,
            request.IsAdministrator
        }))));
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        CompanyMembershipRecord? member = null;
        RoleAssignmentRecord? changedRole = null;
        CompanyAdministratorAuditRecord? audit = null;
        try
        {
            // Share the access service's company lock and membership version:
            // granting a role cannot race with suspension of the same member.
            if (database.Database.IsSqlServer())
            {
                var resource = new SqlParameter("@resource", $"aioffice:membership:{authority.TenantId:D}:{authority.CompanyId:D}");
                await database.Database.ExecuteSqlRawAsync("""
                    DECLARE @result int;
                    EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
                    IF @result<0 THROW 51030,'Company administration is temporarily unavailable.',1;
                    """, [resource], cancellationToken);
            }
            await RequireAdminAsync(authority, cancellationToken);
            var permission = new CompanyAdministratorAuditPermissionVerifier(database);
            await permission.RequireAppendOnlyAsync(cancellationToken);
            var previous = await database.CompanyAdministratorAudits.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.OperationId == request.OperationId, cancellationToken);
            if (previous is not null)
            {
                if (previous.ActorUserId != authority.UserId || previous.RequestHash != hash)
                    throw new CompanyMembershipAccessConflictException("operation-conflict");
                await RequireAdminAsync(authority, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Result(authority.CompanyId, previous.TargetUserId, previous.AfterAdministrator, previous.AfterVersion, previous.OperationId);
            }
            member = await database.CompanyMemberships.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.UserId == userId, cancellationToken)
                ?? throw new CompanyMembershipNotFoundException();
            if (member.Version != expected) throw new CompanyMembershipAccessConflictException("stale-version");
            if (!member.IsActive) throw new CompanyMembershipAccessConflictException("inactive-membership");
            var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.Id == userId, cancellationToken) ?? throw new CompanyMembershipNotFoundException();
            if (!user.IsActive) throw new CompanyMembershipAccessConflictException("inactive-user");
            var assignments = await ReadAssignmentsAsync(authority, userId, cancellationToken);
            if (assignments.Length > 256 || assignments.Any(row => !ValidRoleKey(row.RoleKey)))
                throw new CompanyMembershipAccessConflictException("invalid-role-state");
            // Never turn an ambiguous stored alias into new authority. SQL's
            // default collation may reject inserting admin beside ADMIN.
            if (assignments.Any(row => !StringComparer.Ordinal.Equals(row.RoleKey, "admin") &&
                StringComparer.OrdinalIgnoreCase.Equals(row.RoleKey, "admin")))
                throw new CompanyMembershipAccessConflictException("invalid-role-state");
            var before = assignments.Any(row => row.RoleKey == "admin");
            var beforeRoles = assignments.Select(row => row.RoleKey).Order(StringComparer.Ordinal).ToArray();
            var afterRoles = request.IsAdministrator
                ? beforeRoles.Append("admin").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                : beforeRoles.Where(role => role != "admin").ToArray();
            if (afterRoles.Length > 256) throw new CompanyMembershipAccessConflictException("role-limit");
            if (before && !request.IsAdministrator)
            {
                var candidates = await database.RoleAssignments.AsNoTracking()
                    .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.RoleKey == "admin")
                    .Join(database.CompanyMemberships.Where(row => row.IsActive),
                        role => new { role.TenantId, role.CompanyId, role.UserId }, row => new { row.TenantId, row.CompanyId, row.UserId }, (role, _) => role)
                    .Join(database.Users.Where(row => row.IsActive), role => new { role.TenantId, Id = role.UserId }, row => new { row.TenantId, row.Id }, (role, _) => role)
                    .Take(10_001).Select(role => new { role.UserId, role.RoleKey }).ToArrayAsync(cancellationToken);
                var admins = candidates.Where(row => row.RoleKey == "admin").Select(row => row.UserId).Distinct().ToArray();
                if (candidates.Length > 10_000) throw new InvalidOperationException("Company administration is unavailable.");
                if (admins.Length <= 1 || !admins.Contains(userId))
                    throw new CompanyMembershipAccessConflictException("last-administrator");
            }
            var beforeVersion = member.Version;
            if (before != request.IsAdministrator)
            {
                if (member.Version == long.MaxValue) throw new CompanyMembershipAccessConflictException("version-limit");
                foreach (var tracked in database.ChangeTracker.Entries<CompanyMembershipRecord>().Where(entry =>
                    entry.Entity.TenantId == authority.TenantId && entry.Entity.CompanyId == authority.CompanyId && entry.Entity.UserId == userId).ToArray())
                    tracked.State = EntityState.Detached;
                database.Attach(member);
                member.Version++;
                foreach (var tracked in database.ChangeTracker.Entries<RoleAssignmentRecord>().Where(entry =>
                    entry.Entity.TenantId == authority.TenantId && entry.Entity.CompanyId == authority.CompanyId && entry.Entity.UserId == userId && entry.Entity.RoleKey == "admin").ToArray())
                    tracked.State = EntityState.Detached;
                changedRole = before ? assignments.Single(row => row.RoleKey == "admin") : new()
                {
                    TenantId = authority.TenantId,
                    CompanyId = authority.CompanyId,
                    UserId = userId,
                    RoleKey = "admin",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                if (request.IsAdministrator) database.RoleAssignments.Add(changedRole);
                else database.RoleAssignments.Remove(changedRole);
            }
            audit = new()
            {
                TenantId = authority.TenantId,
                CompanyId = authority.CompanyId,
                Id = Guid.NewGuid(),
                ActorUserId = authority.UserId,
                TargetUserId = userId,
                OperationId = request.OperationId,
                RequestHash = hash,
                BeforeAdministrator = before,
                AfterAdministrator = request.IsAdministrator,
                BeforeRolesJson = JsonSerializer.Serialize(beforeRoles),
                AfterRolesJson = JsonSerializer.Serialize(afterRoles),
                BeforeVersion = beforeVersion,
                AfterVersion = member.Version,
                OccurredAtUtc = DateTimeOffset.UtcNow
            };
            database.CompanyAdministratorAudits.Add(audit);
            await RequireAdminAsync(authority, cancellationToken);
            await permission.RequireAppendOnlyAsync(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result(authority.CompanyId, userId, request.IsAdministrator, member.Version, request.OperationId);
        }
        catch
        {
            if (member is not null) database.Entry(member).State = EntityState.Detached;
            if (changedRole is not null) database.Entry(changedRole).State = EntityState.Detached;
            if (audit is not null) database.Entry(audit).State = EntityState.Detached;
            throw;
        }
    }

    private async Task RequireAdminAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        var current = await directory.ResolveAsync(authority, cancellationToken);
        if (current?.Context != authority || !current.Roles.Contains("admin", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Company administration is unavailable.");
    }
    private async Task<RoleAssignmentRecord[]> ReadAssignmentsAsync(AuthorizationContext authority, Guid userId, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlServer())
            return await database.RoleAssignments.AsNoTracking().Where(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.UserId == userId)
                .OrderBy(row => row.RoleKey).Take(257).ToArrayAsync(cancellationToken);
        // Preserve the complete stored role snapshot. SqlClient repairs invalid
        // UTF16 strings; a repaired audit must not describe different old roles.
        var rows = await database.Database.SqlQuery<StoredRole>($"""
            SELECT TenantId,CompanyId,UserId,CreatedAtUtc,CONVERT(varbinary(max),RoleKey) AS RoleKeyBytes
            FROM aioffice.RoleAssignments
            """).Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.UserId == userId)
            .Take(257).ToArrayAsync(cancellationToken);
        var decoder = new UnicodeEncoding(false, false, true);
        try
        {
            return rows.Select(row => new RoleAssignmentRecord
            {
                TenantId = row.TenantId,
                CompanyId = row.CompanyId,
                UserId = row.UserId,
                CreatedAtUtc = row.CreatedAtUtc,
                RoleKey = row.RoleKeyBytes is { Length: > 0 and <= 200 } bytes && bytes.Length % 2 == 0
                    ? decoder.GetString(bytes) : throw new CompanyMembershipAccessConflictException("invalid-role-state")
            }).ToArray();
        }
        catch (DecoderFallbackException) { throw new CompanyMembershipAccessConflictException("invalid-role-state"); }
    }

    private static bool ValidRoleKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Trim() != key) return false;
        for (var index = 0; index < key.Length; index++)
        {
            if (char.IsControl(key[index]) || char.IsLowSurrogate(key[index])) return false;
            if (!char.IsHighSurrogate(key[index])) continue;
            if (++index == key.Length || !char.IsLowSurrogate(key[index])) return false;
        }
        return true;
    }

    private sealed class StoredRole
    {
        public Guid TenantId { get; set; }
        public Guid CompanyId { get; set; }
        public Guid UserId { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public byte[] RoleKeyBytes { get; set; } = [];
    }
    private static CompanyAdministratorResult Result(Guid company, Guid user, bool administrator, long version, Guid operation) =>
        new(company, user, administrator, version.ToString(CultureInfo.InvariantCulture), operation);
}
