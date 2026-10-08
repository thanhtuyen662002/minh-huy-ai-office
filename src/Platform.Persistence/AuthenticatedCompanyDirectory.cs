using System.Text;
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
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

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
        var sqlServer = database.Database.IsSqlServer();
        users = sqlServer
            ? users.Where(user => EF.Functions.Collate(user.IdentityProvider, "Latin1_General_100_BIN2") == identityProvider
                && EF.Functions.Collate(user.Subject, "Latin1_General_100_BIN2") == subject)
            : users.Where(user => user.IdentityProvider == identityProvider && user.Subject == subject);
        // SqlClient can repair invalid nvarchar sequences while reading a string.
        // Preserve the stored bytes in the same bounded joined query instead.
        var companies = sqlServer
            ? database.Database.SqlQuery<CompanyNameCandidate>($"""
                SELECT Id, TenantId, N'' AS Name, CONVERT(varbinary(max), Name) AS NameBytes
                FROM aioffice.Companies WHERE IsActive = 1
                """)
            : database.Companies.AsNoTracking().Where(company => company.IsActive)
                .Select(company => new CompanyNameCandidate(company.Id, company.TenantId, company.Name, null));
        var rows = await users.Join(database.CompanyMemberships.AsNoTracking().Where(member => member.IsActive),
                user => new { user.TenantId, UserId = user.Id },
                member => new { member.TenantId, member.UserId }, (user, member) => new { user, member })
            .Join(companies,
                row => new { row.member.TenantId, CompanyId = row.member.CompanyId },
                company => new { company.TenantId, CompanyId = company.Id },
                (row, company) => new
                {
                    company.Id,
                    company.Name,
                    company.NameBytes,
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
                || row.Id == Guid.Empty || row.TenantId == Guid.Empty || row.UserId == Guid.Empty)
            || rows.GroupBy(row => row.Id).Any(group => group.Count() != 1)
            || rows.GroupBy(row => row.TenantId).Any(group => group.Select(row => row.UserId).Distinct().Count() != 1))
            throw new UnauthorizedAccessException();

        try
        {
            return rows.Select(row =>
            {
                if (sqlServer && (row.NameBytes is null || row.NameBytes.Length > 400 || row.NameBytes.Length % 2 != 0))
                    throw new UnauthorizedAccessException();
                var name = sqlServer ? StrictUtf16.GetString(row.NameBytes!) : row.Name;
                if (string.IsNullOrWhiteSpace(name) || name.Length > 200
                    || !StringComparer.Ordinal.Equals(name, name.Trim()) || name.Any(char.IsControl) || HasInvalidUnicode(name))
                    throw new UnauthorizedAccessException();
                return new AuthenticatedCompany(row.Id, name);
            }).ToArray();
        }
        catch (DecoderFallbackException) { throw new UnauthorizedAccessException(); }
    }

    private static bool HasInvalidUnicode(string value)
    {
        // Reject malformed stored UTF-16 before JSON can replace it with U+FFFD.
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 == value.Length || !char.IsLowSurrogate(value[++index])) return true;
            }
            else if (char.IsLowSurrogate(value[index])) return true;
        }
        return false;
    }
}

internal sealed record CompanyNameCandidate(Guid Id, Guid TenantId, string Name, byte[]? NameBytes);
