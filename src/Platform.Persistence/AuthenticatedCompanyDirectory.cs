using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record AuthenticatedCompany(Guid CompanyId, string CompanyName);

public interface IAuthenticatedCompanyDirectory
{
    Task<IReadOnlyList<AuthenticatedCompany>> ListAsync(string identityProvider, string subject,
        CancellationToken cancellationToken = default);
}

public sealed class EfAuthenticatedCompanyDirectory(PlatformDbContext database) : IAuthenticatedCompanyDirectory
{
    public const int MaximumCompanies = 100;

    public async Task<IReadOnlyList<AuthenticatedCompany>> ListAsync(string identityProvider, string subject,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(identityProvider) || string.IsNullOrWhiteSpace(subject)
            || identityProvider.Length > 100 || subject.Length > 200)
            throw new UnauthorizedAccessException();

        var users = database.Users.AsNoTracking().Where(user => user.IsActive);
        // Candidate equality must not fold opaque signed identity keys. SQL
        // padding aliases are rejected again against the materialized keys.
        users = database.Database.IsSqlServer()
            ? users.Where(user => EF.Functions.Collate(user.IdentityProvider, "Latin1_General_100_BIN2") == identityProvider
                && EF.Functions.Collate(user.Subject, "Latin1_General_100_BIN2") == subject)
            : users.Where(user => user.IdentityProvider == identityProvider && user.Subject == subject);
        var rows = await users.Join(database.CompanyMemberships.AsNoTracking().Where(member => member.IsActive),
                user => new { user.TenantId, UserId = user.Id },
                member => new { member.TenantId, member.UserId }, (user, member) => new { user, member })
            .Join(database.Companies.AsNoTracking().Where(company => company.IsActive),
                row => new { row.member.TenantId, CompanyId = row.member.CompanyId },
                company => new { company.TenantId, CompanyId = company.Id },
                (row, company) => new
                {
                    company.Id,
                    company.Name,
                    company.TenantId,
                    row.member.UserId,
                    row.user.IdentityProvider,
                    row.user.Subject
                })
            .OrderBy(row => row.Id).ThenBy(row => row.TenantId).ThenBy(row => row.UserId)
            .Take(MaximumCompanies + 1).ToArrayAsync(cancellationToken);

        if (rows.Length > MaximumCompanies
            || rows.Any(row => !StringComparer.Ordinal.Equals(row.IdentityProvider, identityProvider)
                || !StringComparer.Ordinal.Equals(row.Subject, subject)
                || row.Id == Guid.Empty || row.TenantId == Guid.Empty || row.UserId == Guid.Empty
                || string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 200
                || !StringComparer.Ordinal.Equals(row.Name, row.Name.Trim()) || row.Name.Any(char.IsControl))
            || rows.GroupBy(row => row.Id).Any(group => group.Count() != 1)
            || rows.GroupBy(row => row.TenantId).Any(group => group.Select(row => row.UserId).Distinct().Count() != 1))
            throw new UnauthorizedAccessException();

        return rows.Select(row => new AuthenticatedCompany(row.Id, row.Name)).ToArray();
    }
}
