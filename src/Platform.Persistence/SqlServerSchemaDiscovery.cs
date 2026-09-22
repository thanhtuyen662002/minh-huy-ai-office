using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using MinhHuy.AiOffice.Shared.Contracts.Erp;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class SqlServerSchemaDiscovery(ISqlConnectionFactory connectionFactory)
{
    internal const string DiscoverySql = """
        SELECT object_kind, schema_name, object_name, definition_text
        FROM dbo.AIOfficeSchemaDiscoveryView
        ORDER BY object_kind, schema_name, object_name;
        """;

    public async ValueTask<ErpSchemaSnapshot> DiscoverAsync(
        string tenantId,
        string companyId,
        string dataSourceId,
        long version,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var authority = new ErpSchemaSnapshot(tenantId, companyId, dataSourceId, version, []);
        authority.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = DiscoverySql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<(string Kind, string Schema, string Name, string Definition)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }

        return Materialize(authority, rows);
    }

    internal static ErpSchemaSnapshot Materialize(
        ErpSchemaSnapshot authority,
        IEnumerable<(string Kind, string Schema, string Name, string Definition)> rows)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(rows);
        authority.Validate();

        var objects = rows
            .Select(row => new ErpSchemaObject(
                ParseKind(row.Kind),
                RequireMetadataValue(row.Schema, "schema_name"),
                RequireMetadataValue(row.Name, "object_name"),
                HashDefinition(row.Definition ?? string.Empty)))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        return (authority with { Objects = objects }).Validate();
    }

    internal static ErpSchemaObjectKind ParseKind(string value) => value switch
    {
        "TABLE" => ErpSchemaObjectKind.Table,
        "VIEW" => ErpSchemaObjectKind.View,
        "PROCEDURE" => ErpSchemaObjectKind.StoredProcedure,
        "FUNCTION" => ErpSchemaObjectKind.Function,
        "TRIGGER" => ErpSchemaObjectKind.Trigger,
        "FOREIGN_KEY" => ErpSchemaObjectKind.ForeignKey,
        "INDEX" => ErpSchemaObjectKind.Index,
        _ => throw new InvalidOperationException("Unsupported SQL Server schema object kind.")
    };

    internal static string HashDefinition(string definition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));

    private static string RequireMetadataValue(string value, string column)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new InvalidOperationException($"SQL Server schema metadata column {column} is not canonical.");
        return value;
    }
}
