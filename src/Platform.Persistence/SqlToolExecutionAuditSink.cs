using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Durable append-only audit sink. The application surface intentionally exposes INSERT only;
/// the migration also denies UPDATE/DELETE to the public database role.
/// </summary>
public sealed class SqlToolExecutionAuditSink(PlatformDbContext dbContext) : IToolExecutionAuditSink
{
    public async Task AppendAsync(
        ToolExecutionAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO [aioffice].[ToolExecutionAudit]
                    ([AuditId], [TenantId], [CompanyId], [UserId], [TaskId], [Resource], [Action], [Risk], [Authorized], [DecisionReason], [OccurredAtUtc], [ExecutionId])
                VALUES
                    (@auditId, @tenantId, @companyId, @userId, @taskId, @resource, @action, @risk, @authorized, @decisionReason, @occurredAtUtc, @executionId);
                """;

            AddParameter(command, "@auditId", entry.AuditId);
            AddParameter(command, "@tenantId", entry.TenantId);
            AddParameter(command, "@companyId", entry.CompanyId);
            AddParameter(command, "@userId", entry.UserId);
            AddParameter(command, "@taskId", entry.TaskId);
            AddParameter(command, "@resource", entry.Resource);
            AddParameter(command, "@action", entry.Action);
            AddParameter(command, "@risk", entry.Risk.ToString());
            AddParameter(command, "@authorized", entry.Authorized);
            AddParameter(command, "@decisionReason", entry.DecisionReason);
            AddParameter(command, "@occurredAtUtc", entry.OccurredAtUtc);
            AddParameter(command, "@executionId", (object?)entry.ExecutionId ?? DBNull.Value);

            var rows = await command.ExecuteNonQueryAsync(cancellationToken);
            if (rows != 1)
            {
                throw new InvalidOperationException($"Audit append affected {rows} rows instead of exactly one.");
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
