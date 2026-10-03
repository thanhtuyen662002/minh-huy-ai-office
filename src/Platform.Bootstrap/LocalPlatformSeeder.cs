using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Platform.Bootstrap;

public sealed class LocalPlatformSeeder(PlatformDbContext database)
{
    public const string InstallationKey = "local-installation-v1";

    public async Task SeedAsync(BootstrapOptions options, string subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
            throw new InvalidOperationException("A valid OIDC subject is required.");
        var identity = string.Join(':', options.InstallationId, options.TenantId,
            options.CompanyId, options.UserId, options.DataSourceId, subject);
        var existing = await database.PlatformMetadata.SingleOrDefaultAsync(row => row.Key == InstallationKey);
        if (existing is not null)
        {
            if (existing.Value != identity)
                throw new InvalidOperationException("Installation or identity changed; restore the original installation state.");
            // Do not reactivate accounts, reset roles or replace user data on repeat setup.
            return;
        }
        if (await database.Users.AnyAsync() || await database.Companies.AnyAsync())
            throw new InvalidOperationException("Local bootstrap refuses an existing unowned installation.");

        database.Users.Add(new PlatformUserRecord
        {
            TenantId = options.TenantId,
            Id = options.UserId,
            IdentityProvider = BootstrapOptions.Provider,
            Subject = subject,
            DisplayName = "Local Owner"
        });
        database.Companies.Add(new CompanyRecord
        {
            TenantId = options.TenantId,
            Id = options.CompanyId,
            Code = "LOCAL",
            Name = "Minh Huy Local"
        });
        database.CompanyMemberships.Add(new CompanyMembershipRecord
        {
            TenantId = options.TenantId,
            CompanyId = options.CompanyId,
            UserId = options.UserId
        });
        database.RoleAssignments.Add(new RoleAssignmentRecord
        {
            TenantId = options.TenantId,
            CompanyId = options.CompanyId,
            UserId = options.UserId,
            RoleKey = "admin"
        });
        database.DataSources.Add(new DataSourceRecord
        {
            TenantId = options.TenantId,
            CompanyId = options.CompanyId,
            Id = options.DataSourceId,
            LogicalName = "Local sample ERP",
            Kind = "sql-server",
            Environment = "Development",
            Purpose = "Read-only local sample data",
            ConnectionSecretReference = "secretref://env/PILOT_ERP_CONNECTION",
            AllowRead = true,
            AllowWrite = false,
            MaxConcurrency = 2
        });
        database.PlatformMetadata.Add(new PlatformMetadataRecord { Key = InstallationKey, Value = identity });
        // EF SaveChanges uses a SQL transaction, including the installation marker.
        await database.SaveChangesAsync();
    }
}
