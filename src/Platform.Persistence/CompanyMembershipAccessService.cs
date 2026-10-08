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
public sealed record CompanyMembershipAccessRequest(
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] bool IsActive);

public sealed record CompanyMembershipAccessResult(Guid CompanyId, Guid UserId, bool MembershipActive,
    string MembershipVersion, Guid OperationId);
public sealed class CompanyMembershipAccessConflictException(string code = "state-conflict") : Exception("Membership access conflicts with current state.")
{
    public string Code { get; } = code;
}
public sealed class CompanyMembershipNotFoundException() : Exception("Membership is unavailable.");

public sealed class CompanyMembershipAccessService(PlatformDbContext database, IAuthorizationDirectory directory)
{
    public async Task<CompanyMembershipAccessResult> SetAccessAsync(AuthorizationContext authority, Guid userId,
        CompanyMembershipAccessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (userId == Guid.Empty || request.OperationId == Guid.Empty ||
            !long.TryParse(request.ExpectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) ||
            expected < 1 || expected.ToString(CultureInfo.InvariantCulture) != request.ExpectedVersion)
            throw new ArgumentException("Invalid membership access request.");
        // This operation owns its transaction and changes. Do not accidentally
        // commit unrelated pending changes from a reused request context.
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Membership access is unavailable.");
        await RequireAdminAsync(authority, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            authority.UserId,
            TargetUserId = userId,
            ExpectedVersion = expected,
            request.IsActive
        }))));
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        CompanyMembershipRecord? member = null;
        CompanyMembershipAccessAuditRecord? audit = null;
        try
        {
            // One company lock serializes opposite-target admin changes. The
            // serializable directory/target reads also fence external revokers.
            if (database.Database.IsSqlServer())
            {
                var resource = new SqlParameter("@resource", $"aioffice:membership:{authority.TenantId:D}:{authority.CompanyId:D}");
                await database.Database.ExecuteSqlRawAsync("""
                    DECLARE @result int;
                    EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
                    IF @result<0 THROW 51030,'Membership access is temporarily unavailable.',1;
                    """, [resource], cancellationToken);
            }
            await RequireAdminAsync(authority, cancellationToken);
            await new CompanyMembershipAuditPermissionVerifier(database).RequireAppendOnlyAsync(cancellationToken);
            var previous = await database.CompanyMembershipAccessAudits.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.OperationId == request.OperationId, cancellationToken);
            if (previous is not null)
            {
                if (previous.ActorUserId != authority.UserId || previous.RequestHash != hash)
                    throw new CompanyMembershipAccessConflictException("operation-conflict");
                await RequireAdminAsync(authority, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Result(authority.CompanyId, previous.TargetUserId, previous.AfterActive, previous.AfterVersion, previous.OperationId);
            }
            member = await database.CompanyMemberships.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.UserId == userId, cancellationToken)
                ?? throw new CompanyMembershipNotFoundException();
            if (member.Version != expected) throw new CompanyMembershipAccessConflictException("stale-version");
            if (!request.IsActive && userId == authority.UserId) throw new CompanyMembershipAccessConflictException("self-deactivation");
            var targetUser = await database.Users.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == authority.TenantId && row.Id == userId, cancellationToken) ?? throw new CompanyMembershipNotFoundException();
            if (request.IsActive && !targetUser.IsActive) throw new CompanyMembershipAccessConflictException("inactive-user");
            if (!request.IsActive && member.IsActive)
            {
                var candidates = await database.RoleAssignments.AsNoTracking()
                    .Where(role => role.TenantId == authority.TenantId && role.CompanyId == authority.CompanyId && role.RoleKey == "admin")
                    .Join(database.CompanyMemberships.Where(row => row.IsActive),
                        role => new { role.TenantId, role.CompanyId, role.UserId }, row => new { row.TenantId, row.CompanyId, row.UserId }, (role, row) => role)
                    .Join(database.Users.Where(row => row.IsActive), role => new { role.TenantId, Id = role.UserId }, row => new { row.TenantId, row.Id }, (role, row) => role)
                    .Take(10_001).Select(role => new { role.UserId, role.RoleKey }).ToArrayAsync(cancellationToken);
                // SQL collations can match ADMIN or trailing spaces. Only the
                // exact server role used by the directory counts as admin.
                var admins = candidates.Where(role => role.RoleKey == "admin").Select(role => role.UserId).Distinct().ToArray();
                if (candidates.Length > 10_000) throw new InvalidOperationException("Membership access is unavailable.");
                if (admins.Length == 0 || admins.Contains(userId) && admins.Length == 1)
                    throw new CompanyMembershipAccessConflictException("last-administrator");
            }
            var beforeVersion = member.Version;
            var beforeActive = member.IsActive;
            if (member.IsActive != request.IsActive)
            {
                if (member.Version == long.MaxValue) throw new CompanyMembershipAccessConflictException("version-limit");
                var tracked = database.ChangeTracker.Entries<CompanyMembershipRecord>().FirstOrDefault(entry =>
                    entry.Entity.TenantId == authority.TenantId && entry.Entity.CompanyId == authority.CompanyId && entry.Entity.UserId == userId);
                if (tracked is not null) tracked.State = EntityState.Detached;
                database.Attach(member);
                member.IsActive = request.IsActive;
                member.Version++;
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
                BeforeActive = beforeActive,
                AfterActive = member.IsActive,
                BeforeVersion = beforeVersion,
                AfterVersion = member.Version,
                OccurredAtUtc = DateTimeOffset.UtcNow
            };
            database.CompanyMembershipAccessAudits.Add(audit);
            await RequireAdminAsync(authority, cancellationToken);
            await new CompanyMembershipAuditPermissionVerifier(database).RequireAppendOnlyAsync(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result(authority.CompanyId, userId, member.IsActive, member.Version, request.OperationId);
        }
        catch
        {
            if (member is not null) database.Entry(member).State = EntityState.Detached;
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
    private static CompanyMembershipAccessResult Result(Guid company, Guid user, bool active, long version, Guid operation) =>
        new(company, user, active, version.ToString(CultureInfo.InvariantCulture), operation);
}
