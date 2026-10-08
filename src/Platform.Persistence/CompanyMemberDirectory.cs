using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json.Serialization;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record CompanyMemberView(Guid UserId, string DisplayName, bool UserActive, bool MembershipActive, IReadOnlyList<string> Roles,
    [property: JsonIgnore] string MembershipVersion = "1");
public sealed record CompanyMemberPage(Guid CompanyId, IReadOnlyList<CompanyMemberView> Items, int Offset, int Limit, bool HasMore);

public sealed class CompanyMemberDirectory(PlatformDbContext database, IAuthorizationDirectory directory)
{
    public async Task<CompanyMemberPage> ListAsync(AuthorizationContext authority, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(authority, cancellationToken);
        if (offset is < 0 or > 1000 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(offset));

        var rows = await database.CompanyMemberships.AsNoTracking()
            .Where(member => member.TenantId == authority.TenantId && member.CompanyId == authority.CompanyId)
            .Join(database.Users.AsNoTracking().Where(user => user.TenantId == authority.TenantId),
                member => new { member.TenantId, Id = member.UserId }, user => new { user.TenantId, user.Id },
                (member, user) => new { UserId = user.Id, user.DisplayName, UserActive = user.IsActive, MembershipActive = member.IsActive, member.Version })
            .OrderBy(row => row.UserId).Skip(offset).Take(limit + 1).ToArrayAsync(cancellationToken);
        var members = rows.Take(limit).ToArray();
        var userIds = members.Select(member => member.UserId).ToArray();
        // Read only the selected page. Refuse overlarge role collections rather
        // than publishing an incomplete inventory as authoritative.
        var roles = await database.RoleAssignments.AsNoTracking()
            .Where(role => role.TenantId == authority.TenantId && role.CompanyId == authority.CompanyId && userIds.Contains(role.UserId))
            .OrderBy(role => role.UserId).ThenBy(role => role.RoleKey)
            .Take(members.Length * 256 + 1).Select(role => new { role.UserId, role.RoleKey }).ToArrayAsync(cancellationToken);
        if (roles.Length > members.Length * 256 || roles.GroupBy(role => role.UserId).Any(group => group.Count() > 256))
            throw new InvalidOperationException("Company directory is unavailable.");
        var roleMap = roles.GroupBy(role => role.UserId).ToDictionary(group => group.Key,
            group => (IReadOnlyList<string>)group.Select(role => role.RoleKey).Order(StringComparer.Ordinal).ToArray());
        var items = members.Select(member => new CompanyMemberView(member.UserId, member.DisplayName,
            member.UserActive, member.MembershipActive, roleMap.GetValueOrDefault(member.UserId) ?? [], member.Version.ToString(CultureInfo.InvariantCulture))).ToArray();

        // Revocation between the initial authorization and the data query must
        // not turn this response into a retained privileged directory snapshot.
        await RequireAdminAsync(authority, cancellationToken);
        return new(authority.CompanyId, items, offset, limit, rows.Length > limit);
    }

    private async Task RequireAdminAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var entry = await directory.ResolveAsync(authority, cancellationToken);
        if (entry is null || entry.Context.TenantId != authority.TenantId || entry.Context.CompanyId != authority.CompanyId
            || entry.Context.UserId != authority.UserId || !entry.Roles.Contains("admin", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Company administration is unavailable.");
    }
}
