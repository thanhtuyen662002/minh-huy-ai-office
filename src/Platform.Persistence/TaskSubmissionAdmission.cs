using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed class TaskSubmissionAdmission(PlatformDbContext database, IAuthorizationDirectory directory,
    DataSourceSecretBindingService bindings)
{
    internal async Task RequireAuthorityAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var current = await directory.ResolveAsync(authority, cancellationToken);
        if (current?.Context != authority) throw new UnauthorizedAccessException("An active company membership is required.");
    }

    // All submission and membership/role mutations use this ordering. The
    // serializable transaction also holds the user/source/binding read locks
    // against external revocation, including databases with RCSI enabled.
    internal async Task LockCompanyAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlServer()) return;
        if (database.Database.CurrentTransaction is null) throw new InvalidOperationException("Submission requires a transaction.");
        var resource = new SqlParameter("@resource", $"aioffice:membership:{authority.TenantId:D}:{authority.CompanyId:D}");
        await database.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
            IF @result<0 THROW 51031,'Task submission is temporarily unavailable.',1;
            """, [resource], cancellationToken);
    }

    internal async Task RequireExecutionAsync(AuthorizationContext authority, Guid sourceId, CancellationToken cancellationToken)
    {
        await RequireAuthorityAsync(authority, cancellationToken);
        await bindings.RequireSourceAsync(authority, sourceId, readOnly: true, cancellationToken);
        var source = await database.DataSources.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.Id == sourceId, cancellationToken);
        if (source is null || !source.IsEnabled || !source.AllowRead || source.MaxConcurrency is < 1 or > 1024
            || !SecretReference.TryParse(source.ConnectionSecretReference, out _))
            throw new UnauthorizedAccessException("The requested data source is not available for read-only execution.");
        await RequireAuthorityAsync(authority, cancellationToken);
    }
}
