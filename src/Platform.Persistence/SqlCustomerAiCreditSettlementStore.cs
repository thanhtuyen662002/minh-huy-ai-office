using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class SqlCustomerAiCreditSettlementStore(PlatformDbContext dbContext) : ICustomerAiCreditSettlementStore
{
    public Task<CustomerAiCreditSettlementEvidence?> FindBySettlementIdAsync(string settlementId, CancellationToken cancellationToken = default) =>
        FindAsync("[SettlementId]", settlementId, cancellationToken);

    public Task<CustomerAiCreditSettlementEvidence?> FindByReservationIdAsync(string reservationId, CancellationToken cancellationToken = default) =>
        FindAsync("[ReservationId]", reservationId, cancellationToken);

    public async Task<CustomerAiCreditSettlementEvidence> PersistAsync(CustomerAiCreditSettlementEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var bySettlement = await FindAsync(connection, transaction, "[SettlementId]", evidence.SettlementId, true, cancellationToken);
            var byReservation = await FindAsync(connection, transaction, "[ReservationId]", evidence.ReservationId, true, cancellationToken);
            foreach (var existing in new[] { bySettlement, byReservation }.OfType<CustomerAiCreditSettlementEvidence>())
                if (existing != evidence)
                    throw new InvalidOperationException("Settlement or reservation identity already has conflicting persisted evidence.");
            if (bySettlement is not null || byReservation is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return evidence;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO [aioffice].[CustomerAiCreditSettlements]
                    ([SettlementId], [ReservationId], [TenantId], [CompanyId], [AuthorityVersion],
                     [PricingPolicyId], [PricingPolicyVersion], [SettledAiCredits], [ReleasedAiCredits])
                VALUES
                    (@settlementId, @reservationId, @tenantId, @companyId, @authorityVersion,
                     @pricingPolicyId, @pricingPolicyVersion, @settledAiCredits, @releasedAiCredits);
                """;
            Add(command, "@settlementId", evidence.SettlementId);
            Add(command, "@reservationId", evidence.ReservationId);
            Add(command, "@tenantId", evidence.Authority.TenantId);
            Add(command, "@companyId", evidence.Authority.CompanyId);
            Add(command, "@authorityVersion", evidence.Authority.AuthorityVersion);
            Add(command, "@pricingPolicyId", evidence.PricingPolicyId);
            Add(command, "@pricingPolicyVersion", evidence.PricingPolicyVersion);
            Add(command, "@settledAiCredits", evidence.SettledAiCredits);
            Add(command, "@releasedAiCredits", evidence.ReleasedAiCredits);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return evidence;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private async Task<CustomerAiCreditSettlementEvidence?> FindAsync(string column, string value, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try { return await FindAsync(connection, null, column, value, false, cancellationToken); }
        finally { if (openedHere) await connection.CloseAsync(); }
    }

    private static async Task<CustomerAiCreditSettlementEvidence?> FindAsync(DbConnection connection, DbTransaction? transaction, string column, string value, bool lockRow, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim()) throw new ArgumentException("Identity must be canonical.", nameof(value));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT [SettlementId], [ReservationId], [TenantId], [CompanyId], [AuthorityVersion],
                   [PricingPolicyId], [PricingPolicyVersion], [SettledAiCredits], [ReleasedAiCredits]
            FROM [aioffice].[CustomerAiCreditSettlements]{(lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty)}
            WHERE {column} = @value;
            """;
        Add(command, "@value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(
            reader.GetString(0), reader.GetString(1),
            new CustomerBillingAuthority(reader.GetString(2), reader.GetString(3), reader.GetInt64(4)),
            reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8)) : null;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
