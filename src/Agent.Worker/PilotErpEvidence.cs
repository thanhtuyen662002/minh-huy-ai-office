using System.Data.Common;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Agent.Worker;

public sealed record PilotErpTableEvidence(
    string SchemaName,
    string TableName,
    long ApproximateRowCount)
{
    public void Validate()
    {
        ValidateIdentifier(SchemaName, nameof(SchemaName));
        ValidateIdentifier(TableName, nameof(TableName));
        if (ApproximateRowCount < 0)
        {
            throw new InvalidOperationException("ERP evidence row count cannot be negative.");
        }
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 128
            || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"ERP evidence {name} is invalid.");
        }
    }
}

public sealed record PilotErpEvidence(
    string DatabaseName,
    long TableCount,
    IReadOnlyList<PilotErpTableEvidence> TopTables)
{
    public const int MaximumTables = 20;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabaseName)
            || !string.Equals(DatabaseName, DatabaseName.Trim(), StringComparison.Ordinal)
            || DatabaseName.Length > 128
            || DatabaseName.Any(char.IsControl))
        {
            throw new InvalidOperationException("ERP evidence database name is invalid.");
        }

        if (TableCount < 0)
        {
            throw new InvalidOperationException("ERP evidence table count cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(TopTables);
        if (TopTables.Count > MaximumTables)
        {
            throw new InvalidOperationException("ERP evidence exceeds the bounded table limit.");
        }

        foreach (var table in TopTables)
        {
            ArgumentNullException.ThrowIfNull(table);
            table.Validate();
        }
    }
}

public interface IPilotErpEvidenceReader
{
    ValueTask<PilotErpEvidence> ReadAsync(
        string connectionString,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads only bounded SQL Server catalog evidence using a fixed server-authored query.
/// Customer questions and model output never become SQL text.
/// </summary>
public sealed class SqlServerPilotErpEvidenceReader(
    ISqlConnectionFactory connectionFactory) : IPilotErpEvidenceReader
{
    public const int CommandTimeoutSeconds = 10;

    private const string EvidenceSql = """
        SET NOCOUNT ON;

        SELECT
            CAST(DB_NAME() AS nvarchar(128)) AS database_name,
            COUNT_BIG(*) AS table_count
        FROM sys.tables AS t
        WHERE t.is_ms_shipped = 0;

        SELECT TOP (@maxTables)
            CAST(s.name AS nvarchar(128)) AS schema_name,
            CAST(t.name AS nvarchar(128)) AS table_name,
            CAST(COALESCE(SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END), 0) AS bigint)
                AS approximate_row_count
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.partitions AS p ON p.object_id = t.object_id
        WHERE t.is_ms_shipped = 0
        GROUP BY s.name, t.name
        ORDER BY approximate_row_count DESC, s.name, t.name;
        """;

    public async ValueTask<PilotErpEvidence> ReadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = EvidenceSql;
        command.CommandTimeout = CommandTimeoutSeconds;
        var maxTables = command.CreateParameter();
        maxTables.ParameterName = "@maxTables";
        maxTables.Value = PilotErpEvidence.MaximumTables;
        command.Parameters.Add(maxTables);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("ERP evidence summary result is missing.");
        }

        var databaseName = reader.IsDBNull(0)
            ? throw new InvalidOperationException("ERP evidence database name is missing.")
            : reader.GetString(0);
        var tableCount = reader.GetInt64(1);

        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new InvalidOperationException("ERP evidence table result is missing.");
        }

        var tables = new List<PilotErpTableEvidence>(PilotErpEvidence.MaximumTables);
        while (tables.Count < PilotErpEvidence.MaximumTables
               && await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new PilotErpTableEvidence(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }

        var evidence = new PilotErpEvidence(databaseName, tableCount, tables);
        evidence.Validate();
        return evidence;
    }
}
