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

        var roleProjection = await dbContext.CompanyMemberships
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
            .GroupJoin(
                dbContext.RoleAssignments.AsNoTracking(),
                membership => new
                {
                    membership.TenantId,
                    membership.CompanyId,
                    membership.UserId
                },
                role => new
                {
                    role.TenantId,
                    role.CompanyId,
                    role.UserId
                },
                (membership, roles) => roles
                    .OrderBy(role => role.RoleKey)
                    .Select(role => role.RoleKey)
                    .ToArray())
            .SingleOrDefaultAsync(cancellationToken);

        return roleProjection is null
            ? null
            : new AuthorizationDirectoryEntry(context, roleProjection);
    }
}
