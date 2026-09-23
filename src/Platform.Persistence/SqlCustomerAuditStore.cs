using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Reads durable audit evidence only inside the complete server-derived authority boundary.
/// Filtering all authority dimensions in SQL prevents cross-tenant evidence from entering the projection.
/// </summary>
public sealed class SqlCustomerAuditStore(PlatformDbContext dbContext) : ICustomerAuditStore
{
    public async Task<IReadOnlyList<ToolExecutionAuditEntry>> ListAsync(
        Guid tenantId,
        Guid companyId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || companyId == Guid.Empty || userId == Guid.Empty)
            throw new UnauthorizedAccessException("Customer audit authority must be complete.");

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT [AuditId], [TenantId], [CompanyId], [UserId], [TaskId], [Resource], [Action],
                       [Risk], [Authorized], [DecisionReason], [OccurredAtUtc], [ExecutionId]
                FROM [aioffice].[ToolExecutionAudit]
                WHERE [TenantId] = @tenantId
                  AND [CompanyId] = @companyId
                  AND [UserId] = @userId;
                """;
            AddParameter(command, "@tenantId", tenantId);
            AddParameter(command, "@companyId", companyId);
            AddParameter(command, "@userId", userId);

            var entries = new List<ToolExecutionAuditEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Enum.TryParse<ToolRiskLevel>(reader.GetString(7), out var risk))
                    throw new InvalidOperationException("Stored audit risk is invalid.");

                entries.Add(new ToolExecutionAuditEntry(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
                    reader.GetString(5), reader.GetString(6), risk, reader.GetBoolean(8), reader.GetString(9),
                    reader.GetFieldValue<DateTimeOffset>(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
            }

            return entries;
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
