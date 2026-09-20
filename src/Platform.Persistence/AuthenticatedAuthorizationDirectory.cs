using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record AuthenticatedAuthorizationEntry(
    AuthorizationContext Context,
    IReadOnlyList<string> Roles);

public interface IAuthenticatedAuthorizationDirectory
{
    Task<AuthenticatedAuthorizationEntry?> ResolveAsync(
        string identityProvider,
        string subject,
        Guid companyId,
        CancellationToken cancellationToken = default);
}

public sealed class EfAuthenticatedAuthorizationDirectory(PlatformDbContext dbContext)
    : IAuthenticatedAuthorizationDirectory
{
    public async Task<AuthenticatedAuthorizationEntry?> ResolveAsync(
        string identityProvider,
        string subject,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identityProvider)
            || string.IsNullOrWhiteSpace(subject)
            || companyId == Guid.Empty)
        {
            return null;
        }

        var rows = await dbContext.Users
            .AsNoTracking()
            .Where(user =>
                user.IdentityProvider == identityProvider
                && user.Subject == subject
                && user.IsActive)
            .Join(
                dbContext.CompanyMemberships.AsNoTracking().Where(membership =>
                    membership.CompanyId == companyId && membership.IsActive),
                user => new { user.TenantId, UserId = user.Id },
                membership => new { membership.TenantId, membership.UserId },
                (user, membership) => new { user, membership })
            .Join(
                dbContext.Companies.AsNoTracking().Where(company =>
                    company.Id == companyId && company.IsActive),
                joined => new { joined.membership.TenantId, CompanyId = joined.membership.CompanyId },
                company => new { company.TenantId, CompanyId = company.Id },
                (joined, _) => joined)
            .GroupJoin(
                dbContext.RoleAssignments.AsNoTracking(),
                joined => new
                {
                    joined.membership.TenantId,
                    joined.membership.CompanyId,
                    joined.membership.UserId
                },
                role => new { role.TenantId, role.CompanyId, role.UserId },
                (joined, roles) => new { joined, roles })
            .SelectMany(
                joined => joined.roles.DefaultIfEmpty(),
                (joined, role) => new
                {
                    joined.joined.membership.TenantId,
                    joined.joined.membership.CompanyId,
                    joined.joined.membership.UserId,
                    RoleKey = role == null ? null : role.RoleKey
                })
            .OrderBy(row => row.RoleKey)
            .ToArrayAsync(cancellationToken);

        if (rows.Length == 0)
        {
            return null;
        }

        var identity = rows[0];
        if (rows.Any(row =>
                row.TenantId != identity.TenantId
                || row.CompanyId != identity.CompanyId
                || row.UserId != identity.UserId))
        {
            return null;
        }

        var context = AuthorizationContext.Create(
            identity.TenantId,
            identity.CompanyId,
            identity.UserId);
        var roles = rows
            .Where(row => row.RoleKey is not null)
            .Select(row => row.RoleKey!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new AuthenticatedAuthorizationEntry(context, roles);
    }
}
