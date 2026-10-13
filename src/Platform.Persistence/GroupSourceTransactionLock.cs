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
            ?? throw Failure(GroupSourceLockFailure.MissingTransaction);
        command.CommandTimeout = 10;
        command.CommandText = "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000; SELECT @result;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.DbType = DbType.String;
        parameter.Value = $"aioffice:group-ingest:{source.TenantId:N}/{source.CompanyId:N}/{source.SourceBindingId:N}"; command.Parameters.Add(parameter);
        RequireResult(Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)));
    }

    // Fixed failure metadata for the owned proof only. Public exception type,
    // message, conversion, cancellation and lock policy retain their contracts.
    private const string FailureKey = "AIOffice.GroupSourceLockFailure.v1";
    internal static void RequireResult(int result)
    {
        if (result >= 0) return;
        throw Failure(result switch
        {
            -1 => GroupSourceLockFailure.WaitTimeout,
            -2 => GroupSourceLockFailure.Cancelled,
            -3 => GroupSourceLockFailure.Deadlock,
            _ => GroupSourceLockFailure.Rejected
        });
    }
    internal static GroupSourceLockFailure? FailureOf(Exception error)
    {
        if (error.GetType() != typeof(InvalidOperationException)
            || error.Message != "Group source transaction is not available.") return null;
        return error.Data[FailureKey] is GroupSourceLockFailure kind && Enum.IsDefined(kind) ? kind : null;
    }
    private static InvalidOperationException Failure(GroupSourceLockFailure kind)
    {
        var failure = new InvalidOperationException("Group source transaction is not available.");
        failure.Data[FailureKey] = kind;
        return failure;
    }
}

internal enum GroupSourceLockFailure { MissingTransaction, WaitTimeout, Cancelled, Deadlock, Rejected }
