using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using MinhHuy.AiOffice.Shared.Contracts.Erp;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class SqlServerSchemaDiscovery(ISqlConnectionFactory connectionFactory)
{
    // Read-only catalog projection: no customer-DB view or migration is required. Only
    // schema metadata enters the structural signature; credentials and row data cannot.
    internal const string DiscoverySql = """
        WITH schema_objects AS (
            SELECT
                CASE
                    WHEN o.type = 'U' THEN 'TABLE'
                    WHEN o.type = 'V' THEN 'VIEW'
                    WHEN o.type = 'P' THEN 'PROCEDURE'
                    WHEN o.type IN ('FN', 'IF', 'TF') THEN 'FUNCTION'
                    WHEN o.type = 'TR' THEN 'TRIGGER'
                END AS object_kind,
                s.name AS schema_name,
                o.name AS object_name,
                CASE WHEN o.type = 'U' THEN COALESCE((
                    SELECT STRING_AGG(CAST(CONCAT(c.column_id, ':', c.name, ':', TYPE_NAME(c.user_type_id),
                        ':', c.max_length, ':', c.precision, ':', c.scale, ':nullable=', c.is_nullable,
                        ':identity=', c.is_identity, ':computed=', c.is_computed) AS nvarchar(max)), '|')
                        WITHIN GROUP (ORDER BY c.column_id)
                    FROM sys.columns AS c WHERE c.object_id = o.object_id
                ), '') ELSE COALESCE(m.definition, '') END AS definition_text
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR')
        ),
        foreign_keys AS (
            SELECT
                'FOREIGN_KEY' AS object_kind,
                s.name AS schema_name,
                fk.name AS object_name,
                CONCAT(OBJECT_SCHEMA_NAME(fk.parent_object_id), '.', OBJECT_NAME(fk.parent_object_id),
                       '->', OBJECT_SCHEMA_NAME(fk.referenced_object_id), '.', OBJECT_NAME(fk.referenced_object_id),
                       ':', fk.delete_referential_action_desc, ':', fk.update_referential_action_desc, ':',
                       COALESCE((SELECT STRING_AGG(CAST(CONCAT(fkc.constraint_column_id, ':', pc.name, '->', rc.name) AS nvarchar(max)), '|')
                           WITHIN GROUP (ORDER BY fkc.constraint_column_id)
                           FROM sys.foreign_key_columns AS fkc
                           INNER JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                           INNER JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                           WHERE fkc.constraint_object_id = fk.object_id), '')) AS definition_text
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.objects AS parent_object ON parent_object.object_id = fk.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = parent_object.schema_id
            WHERE fk.is_ms_shipped = 0
        ),
        indexes AS (
            SELECT
                'INDEX' AS object_kind,
                s.name AS schema_name,
                CONCAT(o.name, '.', i.name) AS object_name,
                CONCAT(i.type_desc, ':unique=', i.is_unique, ':filter=', COALESCE(i.filter_definition, ''), ':',
                       COALESCE((SELECT STRING_AGG(CAST(CONCAT(ic.key_ordinal, ':', c.name, ':desc=', ic.is_descending_key,
                           ':include=', ic.is_included_column) AS nvarchar(max)), '|')
                           WITHIN GROUP (ORDER BY ic.key_ordinal, ic.index_column_id)
                           FROM sys.index_columns AS ic
                           INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                           WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id), '')) AS definition_text
            FROM sys.indexes AS i
            INNER JOIN sys.objects AS o ON o.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0 AND i.name IS NOT NULL AND i.is_hypothetical = 0
        )
        SELECT object_kind, schema_name, object_name, definition_text FROM schema_objects
        UNION ALL
        SELECT object_kind, schema_name, object_name, definition_text FROM foreign_keys
        UNION ALL
        SELECT object_kind, schema_name, object_name, definition_text FROM indexes
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
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
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
            .Select(row => new ErpSchemaObject(ParseKind(row.Kind),
                RequireMetadataValue(row.Schema, "schema_name"), RequireMetadataValue(row.Name, "object_name"),
                HashDefinition(row.Definition ?? string.Empty)))
            .OrderBy(item => item.Kind).ThenBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();

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
