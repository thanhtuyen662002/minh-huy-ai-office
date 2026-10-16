using System.Text;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupRegistryReader
{
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    internal static async Task<GroupBindingRecord?> BindingAsync(PlatformDbContext db, string hash, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer()) return await db.GroupBindings.AsNoTracking().SingleOrDefaultAsync(x => x.IdentityHash == hash, cancellationToken);
        var row = await db.Database.SqlQuery<StoredBinding>($"""
            SELECT TenantId,CompanyId,Id,ConnectorAccountId,Role,Provider,IdentityHash,PhysicalGroupHash,Version,DeletionGeneration,IsEnabled,
              CASE WHEN DATALENGTH(ExternalAccountId)<=512 THEN CONVERT(varbinary(512),ExternalAccountId) END AS AccountBytes,
              CASE WHEN DATALENGTH(ExternalGroupId)<=512 THEN CONVERT(varbinary(512),ExternalGroupId) END AS GroupBytes,
              CASE WHEN DATALENGTH(DisplayName)<=400 THEN CONVERT(varbinary(400),DisplayName) END AS DisplayBytes
            FROM aioffice.GroupBindings WHERE IdentityHash={hash}
            """).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        return new()
        {
            TenantId = row.TenantId,
            CompanyId = row.CompanyId,
            Id = row.Id,
            ConnectorAccountId = row.ConnectorAccountId,
            Role = row.Role,
            Provider = row.Provider,
            IdentityHash = row.IdentityHash,
            PhysicalGroupHash = row.PhysicalGroupHash,
            Version = row.Version,
            DeletionGeneration = row.DeletionGeneration,
            IsEnabled = row.IsEnabled,
            ExternalAccountId = Decode(row.AccountBytes, 512),
            ExternalGroupId = Decode(row.GroupBytes, 512),
            DisplayName = Decode(row.DisplayBytes, 400)
        };
    }

    internal static async Task<GroupConnectorAccountRecord?> AccountAsync(PlatformDbContext db, Guid tenant, Guid company, Guid id, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer()) return await db.GroupConnectorAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.CompanyId == company && x.Id == id, cancellationToken);
        var row = await db.Database.SqlQuery<StoredAccount>($"""
            SELECT TenantId,CompanyId,Id,Provider,IdentityHash,PackageVersion,GitCommit,Version,IsEnabled,
              CASE WHEN DATALENGTH(ExternalAccountId)<=512 THEN CONVERT(varbinary(512),ExternalAccountId) END AS AccountBytes,
              CASE WHEN DATALENGTH(QualificationJson)<=32000 THEN CONVERT(varbinary(max),QualificationJson) END AS QualificationBytes
            FROM aioffice.GroupConnectorAccounts WHERE TenantId={tenant} AND CompanyId={company} AND Id={id}
            """).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        return new()
        {
            TenantId = row.TenantId,
            CompanyId = row.CompanyId,
            Id = row.Id,
            Provider = row.Provider,
            IdentityHash = row.IdentityHash,
            PackageVersion = row.PackageVersion,
            GitCommit = row.GitCommit,
            Version = row.Version,
            IsEnabled = row.IsEnabled,
            ExternalAccountId = Decode(row.AccountBytes, 512),
            QualificationJson = Decode(row.QualificationBytes, 32000)
        };
    }

    // The physical-role catalog is operator SQL, never a request-supplied list.
    // Query original exact bytes independently of both indexes: a corrupt index
    // must not conceal another role, account or company for the physical group.
    internal static async Task<bool> IsExclusivePhysicalOwnerAsync(PlatformDbContext db, GroupBindingRecord binding, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
        {
            var rows = await db.GroupBindings.AsNoTracking().Where(x => x.Provider == binding.Provider && x.ExternalGroupId == binding.ExternalGroupId)
                .Take(2).ToListAsync(cancellationToken);
            return rows.Count == 1 && rows[0].TenantId == binding.TenantId && rows[0].CompanyId == binding.CompanyId && rows[0].Id == binding.Id;
        }
        var owners = await db.Database.SqlQuery<StoredPhysicalOwner>($"""
            SELECT TenantId,CompanyId,Id FROM aioffice.GroupBindings
            WHERE Provider COLLATE Latin1_General_100_BIN2={binding.Provider}
              AND DATALENGTH(Provider)=DATALENGTH(CONVERT(varchar(64),{binding.Provider}))
              AND DATALENGTH(ExternalGroupId)=DATALENGTH({binding.ExternalGroupId})
              AND CONVERT(varbinary(512),ExternalGroupId)=CONVERT(varbinary(512),{binding.ExternalGroupId})
            """).Take(2).ToListAsync(cancellationToken);
        return owners.Count == 1 && owners[0].TenantId == binding.TenantId && owners[0].CompanyId == binding.CompanyId && owners[0].Id == binding.Id;
    }

    internal static string Decode(byte[]? bytes, int maximumBytes)
    {
        if (bytes is null || bytes.Length > maximumBytes || bytes.Length % 2 != 0) throw GroupServiceDirectory.Denied();
        try { return StrictUtf16.GetString(bytes); }
        catch (DecoderFallbackException) { throw GroupServiceDirectory.Denied(); }
    }

    private sealed record StoredBinding(Guid TenantId, Guid CompanyId, Guid Id, Guid ConnectorAccountId,
        GroupBindingRole Role, string Provider, string IdentityHash, string PhysicalGroupHash,
        long Version, long DeletionGeneration, bool IsEnabled, byte[]? AccountBytes, byte[]? GroupBytes, byte[]? DisplayBytes);
    private sealed record StoredAccount(Guid TenantId, Guid CompanyId, Guid Id, string Provider, string IdentityHash,
        string PackageVersion, string GitCommit, long Version, bool IsEnabled, byte[]? AccountBytes, byte[]? QualificationBytes);
    private sealed record StoredPhysicalOwner(Guid TenantId, Guid CompanyId, Guid Id);
}
