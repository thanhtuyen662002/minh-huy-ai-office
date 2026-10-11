using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Ingress and scheduling must serialize on this identical commit-owned key.
internal static class GroupSourceTransactionLock
{
    internal static async Task RequireAsync(PlatformDbContext database, GroupScope source, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlServer()) return;
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Group source transaction is not available.");
        command.CommandTimeout = 10;
        command.CommandText = "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000; SELECT @result;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.DbType = DbType.String;
        parameter.Value = $"aioffice:group-ingest:{source.TenantId:N}/{source.CompanyId:N}/{source.SourceBindingId:N}"; command.Parameters.Add(parameter);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
            throw new InvalidOperationException("Group source transaction is not available.");
    }
}
