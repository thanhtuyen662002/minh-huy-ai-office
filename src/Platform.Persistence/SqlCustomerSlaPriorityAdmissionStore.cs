using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// SQL Server authority store for SLA admission evidence. AdmissionId is globally unique and
/// persistence is serialized so concurrent exact replay is idempotent while conflicting replay
/// fails closed before scheduler enqueue. Read lookup is always scoped to server-derived authority.
/// </summary>
public sealed class SqlCustomerSlaPriorityAdmissionStore(PlatformDbContext dbContext)
    : ICustomerSlaPriorityAdmissionStore
{
    public async Task<CustomerSlaPriorityAdmissionEvidence?> FindAsync(
        string admissionId,
        CustomerSlaAuthority authority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(admissionId) || admissionId != admissionId.Trim())
            throw new ArgumentException("AdmissionId must be canonical.", nameof(admissionId));
        ValidateAuthority(authority);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = CreateSelectCommand(connection, null, admissionId, authority, lockRow: false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    public async Task<CustomerSlaPriorityAdmissionEvidence> PersistAsync(
        CustomerSlaPriorityAdmissionEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Validate(evidence);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            CustomerSlaPriorityAdmissionEvidence? existing;
            await using (var select = CreateSelectCommand(connection, transaction, evidence.AdmissionId, null, lockRow: true))
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
                existing = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;

            if (existing is not null)
            {
                if (existing != evidence)
                    throw new InvalidOperationException("Admission identity already has conflicting persisted evidence.");

                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO [aioffice].[CustomerSlaPriorityAdmissions]
                    ([AdmissionId], [TenantId], [CompanyId], [UserId], [AuthorityVersion],
                     [PolicyId], [PolicyVersion], [ServiceClass], [SchedulerPriority], [PriorityCeiling])
                VALUES
                    (@admissionId, @tenantId, @companyId, @userId, @authorityVersion,
                     @policyId, @policyVersion, @serviceClass, @schedulerPriority, @priorityCeiling);
                """;
            Add(insert, "@admissionId", evidence.AdmissionId);
            Add(insert, "@tenantId", evidence.Authority.TenantId);
            Add(insert, "@companyId", evidence.Authority.CompanyId);
            Add(insert, "@userId", evidence.Authority.UserId);
            Add(insert, "@authorityVersion", evidence.Authority.AuthorityVersion);
            Add(insert, "@policyId", evidence.PolicyId);
            Add(insert, "@policyVersion", evidence.PolicyVersion);
            Add(insert, "@serviceClass", evidence.ServiceClass);
            Add(insert, "@schedulerPriority", evidence.SchedulerPriority);
            Add(insert, "@priorityCeiling", evidence.PriorityCeiling);
            await insert.ExecuteNonQueryAsync(cancellationToken);
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
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    private static DbCommand CreateSelectCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string admissionId,
        CustomerSlaAuthority? authority,
        bool lockRow)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var lockHint = lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var authorityPredicate = authority is null
            ? string.Empty
            : " AND [TenantId] = @tenantId AND [CompanyId] = @companyId AND [UserId] = @userId AND [AuthorityVersion] = @authorityVersion";
        command.CommandText = $"""
            SELECT [AdmissionId], [TenantId], [CompanyId], [UserId], [AuthorityVersion],
                   [PolicyId], [PolicyVersion], [ServiceClass], [SchedulerPriority], [PriorityCeiling]
            FROM [aioffice].[CustomerSlaPriorityAdmissions]{lockHint}
            WHERE [AdmissionId] = @admissionId{authorityPredicate};
            """;
        Add(command, "@admissionId", admissionId);
        if (authority is not null)
        {
            Add(command, "@tenantId", authority.TenantId);
            Add(command, "@companyId", authority.CompanyId);
            Add(command, "@userId", authority.UserId);
            Add(command, "@authorityVersion", authority.AuthorityVersion);
        }
        return command;
    }

    private static CustomerSlaPriorityAdmissionEvidence Read(DbDataReader reader) => new(
        reader.GetString(0),
        new CustomerSlaAuthority(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.GetString(7),
        reader.GetInt32(8))
    {
        PriorityCeiling = reader.GetInt32(9),
    };

    private static void Validate(CustomerSlaPriorityAdmissionEvidence evidence)
    {
        Canonical(evidence.AdmissionId, nameof(evidence.AdmissionId));
        ValidateAuthority(evidence.Authority);
        Canonical(evidence.PolicyId, nameof(evidence.PolicyId));
        Canonical(evidence.ServiceClass, nameof(evidence.ServiceClass));
        if (evidence.PolicyVersion <= 0 || evidence.SchedulerPriority < 0 || evidence.PriorityCeiling < 0 ||
            evidence.SchedulerPriority > evidence.PriorityCeiling)
            throw new InvalidOperationException("SLA admission evidence contains invalid policy, priority, or ceiling values.");
    }

    private static void ValidateAuthority(CustomerSlaAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        Canonical(authority.TenantId, nameof(authority.TenantId));
        Canonical(authority.CompanyId, nameof(authority.CompanyId));
        Canonical(authority.UserId, nameof(authority.UserId));
        if (authority.AuthorityVersion <= 0)
            throw new InvalidOperationException("SLA admission authority version must be positive.");
    }

    private static void Canonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be canonical.", name);
        if (value.Length > 200)
            throw new ArgumentOutOfRangeException(name, "Identifier exceeds durable schema limit.");
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
