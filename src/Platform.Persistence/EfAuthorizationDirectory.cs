using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record AuthorizationDirectoryEntry(
    AuthorizationContext Context,
    IReadOnlyList<string> Roles);

public interface IAuthorizationDirectory
{
    Task<AuthorizationDirectoryEntry?> ResolveAsync(
        AuthorizationContext context,
        CancellationToken cancellationToken = default);
}

public sealed class EfAuthorizationDirectory(PlatformDbContext dbContext) : IAuthorizationDirectory
{
    public async Task<AuthorizationDirectoryEntry?> ResolveAsync(
        AuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hasActiveMembership = await dbContext.CompanyMemberships
            .AsNoTracking()
            .Where(membership =>
                membership.TenantId == context.TenantId
                && membership.CompanyId == context.CompanyId
                && membership.UserId == context.UserId
                && membership.IsActive)
            .Join(
                dbContext.Users.AsNoTracking().Where(user => user.IsActive),
                membership => new { membership.TenantId, Id = membership.UserId },
                user => new { user.TenantId, user.Id },
                (membership, _) => membership)
            .Join(
                dbContext.Companies.AsNoTracking().Where(company => company.IsActive),
                membership => new { membership.TenantId, Id = membership.CompanyId },
                company => new { company.TenantId, company.Id },
                (membership, _) => membership)
            .AnyAsync(cancellationToken);

        if (!hasActiveMembership)
        {
            return null;
        }

        var roles = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(role =>
                role.TenantId == context.TenantId
                && role.CompanyId == context.CompanyId
                && role.UserId == context.UserId)
            .OrderBy(role => role.RoleKey)
            .Select(role => role.RoleKey)
            .ToArrayAsync(cancellationToken);

        return new AuthorizationDirectoryEntry(context, roles);
    }
}
