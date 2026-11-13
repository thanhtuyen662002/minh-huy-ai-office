using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>
/// Validates model-proposed ERP SQL before it can reach the read-only connection.
/// Only one current-database SELECT (optionally with CTEs) is allowed.
/// </summary>
public static class ErpAgentSqlGuard
{
    public const int MaximumSqlLength = 8_000;

    private static readonly Regex StartsWithSelect =
        new(@"^(SELECT|WITH)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ForbiddenToken =
        new(
            @"\b(INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|DROP|ALTER|CREATE|TRUNCATE|GRANT|REVOKE|DENY|DBCC|BACKUP|RESTORE|BULK|OPENQUERY|OPENROWSET|OPENDATASOURCE|OPENXML|INTO|WAITFOR|SHUTDOWN|KILL|RECONFIGURE|xp_\w*|sp_\w*)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Returns the statement without a trailing semicolon or throws with a short, model-safe reason.</summary>
    public static string Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw Rejected("SQL is empty.");
        }

        if (sql.Length > MaximumSqlLength)
        {
            throw Rejected($"SQL is longer than {MaximumSqlLength} characters.");
        }

        if (sql.Contains("--", StringComparison.Ordinal)
            || sql.Contains("/*", StringComparison.Ordinal)
            || sql.Contains("*/", StringComparison.Ordinal))
        {
            throw Rejected("SQL comments are not allowed.");
        }

        var statement = sql.Trim();
        if (statement.EndsWith(';'))
        {
            statement = statement[..^1].TrimEnd();
        }

        if (statement.Contains(';'))
        {
            throw Rejected("Only one statement is allowed (no ';').");
        }

        if (!StartsWithSelect.IsMatch(statement))
        {
            throw Rejected("Only SELECT or WITH ... SELECT queries are allowed.");
        }

        var forbidden = ForbiddenToken.Match(statement);
        if (forbidden.Success)
        {
            throw Rejected($"Keyword '{forbidden.Value.ToUpperInvariant()}' is not allowed in read-only queries.");
        }

        using var input = new StringReader(statement);
        var parsed = new TSql160Parser(initialQuotedIdentifiers: true).Parse(input, out var errors);
        if (errors.Count != 0)
        {
            var first = errors[0];
            throw Rejected($"T-SQL parse error near line {first.Line}: {Shorten(first.Message, 160)}");
        }

        if (parsed is not TSqlScript { Batches.Count: 1 } script
            || script.Batches[0].Statements.Count != 1
            || script.Batches[0].Statements[0] is not SelectStatement select)
        {
            throw Rejected("Only a single SELECT statement is allowed.");
        }

        if (select.Into is not null)
        {
            throw Rejected("SELECT INTO is not allowed.");
        }

        parsed.Accept(new CurrentDatabaseReadVisitor());
        return statement;
    }

    private static InvalidOperationException Rejected(string reason) => new(reason);

    internal static string Shorten(string value, int maximum)
    {
        var flat = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return flat.Length <= maximum ? flat : flat[..maximum] + "…";
    }

    private sealed class CurrentDatabaseReadVisitor : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(SchemaObjectName node)
        {
            if (node.Identifiers.Count > 2)
            {
                throw Rejected("Only objects in the current database are allowed (use schema.table, not db.schema.table).");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MultiPartIdentifierCallTarget node)
        {
            if (node.MultiPartIdentifier.Identifiers.Count > 1)
            {
                throw Rejected("Cross-database function calls are not allowed.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NextValueForExpression node) =>
            throw Rejected("NEXT VALUE FOR changes state and is not allowed.");
    }
}

public sealed record ErpAgentQueryResult(
    string Observation,
    int RowCount,
    bool Truncated,
    string? Error);

/// <summary>Operations available to the agent on one open, read-only-verified ERP connection.</summary>
public interface IErpQuerySession
{
    Task<string> FindTablesAsync(IReadOnlyList<string> keywords, CancellationToken cancellationToken);

    Task<string> DescribeTablesAsync(IReadOnlyList<string> tables, CancellationToken cancellationToken);

    /// <summary>Runs SQL that already passed <see cref="ErpAgentSqlGuard.Validate"/>.</summary>
    Task<ErpAgentQueryResult> QueryAsync(string validatedSql, CancellationToken cancellationToken);
}

public interface IErpQueryAgentBackend
{
    /// <param name="sessionContext">Read-only SESSION_CONTEXT values for the whole session (an ERP user's data scopes); null for none.</param>
    Task<T> UseAsync<T>(
        string connectionString,
        Func<IErpQuerySession, Task<T>> use,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? sessionContext = null);
}

/// <summary>The ERP user's data scopes cannot be enforced on this database, so the question is refused (fail closed).</summary>
public sealed class ErpScopeUnavailableException(string message) : Exception(message);

/// <summary>
/// SQL Server backend. The read-only proof runs before and after the whole agent session; any
/// privilege change during the session discards its result.
/// </summary>
public sealed class SqlServerErpQueryAgentBackend(ISqlConnectionFactory connectionFactory) : IErpQueryAgentBackend
{
    public const string NoRowLevelSecurity =
        "Không thể giới hạn dữ liệu theo người dùng ERP: database này chưa bật Row-Level Security (security policy), "
        + "hoặc tài khoản đọc của AI Office không thấy policy đó (cấp GRANT VIEW DEFINITION ON OBJECT::<schema.policy> TO <tài khoản đọc>).";

    public async Task<T> UseAsync<T>(
        string connectionString,
        Func<IErpQuerySession, Task<T>> use,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? sessionContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(use);

        await using var connection = connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (sessionContext is { Count: > 0 })
        {
            await ApplySessionContextAsync(connection, sessionContext, cancellationToken);
        }

        return await ErpReadOnlyConnectionVerifier.ReadAsync(
            connection,
            () => use(new SqlServerErpQuerySession(connection)),
            cancellationToken);
    }

    /// <summary>
    /// Sets each value read-only, so nothing later on this session (the model's SQL included) can change it; pooled
    /// connections clear SESSION_CONTEXT on reset. Customer scopes need an enabled security policy, or nothing would filter.
    /// </summary>
    private static async Task ApplySessionContextAsync(DbConnection connection, IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        foreach (var (key, value) in values)
        {
            await using var set = connection.CreateCommand();
            set.CommandTimeout = SqlServerErpQuerySession.CatalogTimeoutSeconds;
            set.CommandText = "EXEC sys.sp_set_session_context @key = @k, @value = @v, @read_only = 1;";
            AddParameter(set, "@k", key, 128);
            AddParameter(set, "@v", value, 4000);
            await set.ExecuteNonQueryAsync(cancellationToken);
        }

        if (values.Keys.Any(key => !key.StartsWith(ErpChatScopes.ReservedPrefix, StringComparison.Ordinal)))
        {
            await using var check = connection.CreateCommand();
            check.CommandTimeout = SqlServerErpQuerySession.CatalogTimeoutSeconds;
            check.CommandText = "SELECT COUNT(*) FROM sys.security_policies WHERE is_enabled = 1;";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            {
                throw new ErpScopeUnavailableException(NoRowLevelSecurity);
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, string value, int size)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Size = size;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>The SESSION_CONTEXT for a task: the ERP user's id under the reserved key plus its customer scopes; null for members.</summary>
    public static IReadOnlyDictionary<string, string>? SessionContext(string? externalUserId, string? externalScopesJson)
    {
        if (externalUserId is null) return null;
        var values = new Dictionary<string, string>(ErpChatScopes.Parse(externalScopesJson), StringComparer.Ordinal)
        {
            [ErpChatScopes.UserKey] = externalUserId
        };
        return values;
    }
}

public sealed class SqlServerErpQuerySession(DbConnection connection) : IErpQuerySession
{
    public const int MaximumKeywords = 5;
    public const int MaximumKeywordLength = 64;
    public const int MaximumTablesFound = 30;
    public const int MaximumTablesDescribed = 5;
    public const int MaximumColumnsDescribed = 120;
    public const int CatalogTimeoutSeconds = 20;
    public const int QueryTimeoutSeconds = 20;
    public const int MaximumRows = 50;
    public const int MaximumColumns = 20;
    public const int MaximumCellCharacters = 200;
    public const int MaximumResultCharacters = 12_000;

    public async Task<string> FindTablesAsync(IReadOnlyList<string> keywords, CancellationToken cancellationToken)
    {
        var patterns = keywords
            .Select(keyword => keyword?.Trim() ?? string.Empty)
            .Where(keyword => keyword.Length > 0)
            .Select(keyword => keyword.Length > MaximumKeywordLength ? keyword[..MaximumKeywordLength] : keyword)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumKeywords)
            .ToArray();
        if (patterns.Length == 0)
        {
            return "find_tables needs at least one non-empty keyword.";
        }

        var tableMatch = string.Join(" OR ", patterns.Select((_, index) =>
            $"o.name COLLATE Latin1_General_CI_AI LIKE @k{index} ESCAPE N'\\'"));
        var columnMatch = string.Join(" OR ", patterns.Select((_, index) =>
            $"c.name COLLATE Latin1_General_CI_AI LIKE @k{index} ESCAPE N'\\'"));
        var descriptionMatch = string.Join(" OR ", patterns.Select((_, index) =>
            $"CAST(ep.value AS nvarchar(4000)) COLLATE Latin1_General_CI_AI LIKE @k{index} ESCAPE N'\\'"));

        await using var command = connection.CreateCommand();
        command.CommandTimeout = CatalogTimeoutSeconds;
        command.CommandText = $"""
            SELECT TOP ({MaximumTablesFound})
                CAST(s.name AS nvarchar(128)) AS found_schema,
                CAST(o.name AS nvarchar(128)) AS found_object,
                CAST(o.type AS nvarchar(4)) AS found_type,
                CAST(COALESCE((SELECT SUM(p.rows) FROM sys.partitions AS p
                    WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)), 0) AS bigint) AS found_rows,
                CAST((SELECT TOP (1) CAST(ep.value AS nvarchar(300)) FROM sys.extended_properties AS ep
                    WHERE ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0
                      AND ep.name = N'MS_Description') AS nvarchar(300)) AS found_description,
                CAST(STUFF((SELECT TOP (8) N', ' + c.name FROM sys.columns AS c
                    WHERE c.object_id = o.object_id AND ({columnMatch})
                    ORDER BY c.column_id
                    FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'') AS nvarchar(1000)) AS found_columns
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
              AND (({tableMatch})
                OR EXISTS (SELECT 1 FROM sys.columns AS c WHERE c.object_id = o.object_id AND ({columnMatch}))
                OR EXISTS (SELECT 1 FROM sys.extended_properties AS ep
                    WHERE ep.class = 1 AND ep.major_id = o.object_id AND ({descriptionMatch})))
            ORDER BY found_rows DESC, found_schema, found_object;
            """;
        for (var index = 0; index < patterns.Length; index++)
        {
            AddParameter(command, $"@k{index}", "%" + EscapeLike(patterns[index]) + "%");
        }

        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"find_tables [{string.Join(", ", patterns)}]:");
        var count = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                count++;
                var kind = reader.GetString(2).Trim() == "V" ? "view" : "table";
                builder.Append(CultureInfo.InvariantCulture,
                    $"- {reader.GetString(0)}.{reader.GetString(1)} ({kind}, ~{reader.GetInt64(3)} rows)");
                if (!reader.IsDBNull(5) && reader.GetString(5).Length > 0)
                {
                    builder.Append(CultureInfo.InvariantCulture, $" matching columns: {reader.GetString(5)}");
                }

                if (!reader.IsDBNull(4) && reader.GetString(4).Length > 0)
                {
                    builder.Append(CultureInfo.InvariantCulture, $" — {ErpAgentSqlGuard.Shorten(reader.GetString(4), 200)}");
                }

                builder.AppendLine();
            }
        }

        if (count == 0)
        {
            builder.AppendLine("(no table, view or column name matched; try other spellings, Vietnamese without accents, or English)");
        }

        return builder.ToString();
    }

    public async Task<string> DescribeTablesAsync(IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        var names = tables
            .Select(table => table?.Trim() ?? string.Empty)
            .Where(table => table.Length is > 0 and <= 260 && !table.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTablesDescribed)
            .ToArray();
        if (names.Length == 0)
        {
            return "describe needs at least one table name such as dbo.TableName.";
        }

        var builder = new StringBuilder();
        foreach (var name in names)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = CatalogTimeoutSeconds;
            command.CommandText = $"""
                SELECT TOP ({MaximumColumnsDescribed})
                    CAST(OBJECT_SCHEMA_NAME(c.object_id) AS nvarchar(128)) AS schema_name,
                    CAST(OBJECT_NAME(c.object_id) AS nvarchar(128)) AS object_name,
                    CAST(c.name AS nvarchar(128)) AS column_name,
                    CAST(TYPE_NAME(c.user_type_id) AS nvarchar(128)) AS type_name,
                    CAST(c.max_length AS int) AS max_length,
                    CAST(c.is_nullable AS bit) AS is_nullable,
                    CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns AS ic
                        INNER JOIN sys.indexes AS i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                        WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id)
                        THEN 1 ELSE 0 END AS bit) AS is_primary_key,
                    CAST((SELECT TOP (1) OBJECT_SCHEMA_NAME(fk.referenced_object_id) + N'.'
                        + OBJECT_NAME(fk.referenced_object_id) + N'.'
                        + COL_NAME(fk.referenced_object_id, fk.referenced_column_id)
                        FROM sys.foreign_key_columns AS fk
                        WHERE fk.parent_object_id = c.object_id AND fk.parent_column_id = c.column_id) AS nvarchar(400)) AS references_column,
                    CAST((SELECT TOP (1) CAST(ep.value AS nvarchar(300)) FROM sys.extended_properties AS ep
                        WHERE ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id
                          AND ep.name = N'MS_Description') AS nvarchar(300)) AS description
                FROM sys.columns AS c
                INNER JOIN sys.objects AS o ON o.object_id = c.object_id
                WHERE c.object_id = OBJECT_ID(@name) AND o.type IN ('U', 'V') AND o.is_ms_shipped = 0
                ORDER BY c.column_id;
                """;
            AddParameter(command, "@name", name);

            var columns = 0;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (columns == 0)
                    {
                        builder.AppendLine(CultureInfo.InvariantCulture, $"describe {reader.GetString(0)}.{reader.GetString(1)}:");
                    }

                    columns++;
                    var type = reader.GetString(3);
                    var length = reader.GetInt32(4);
                    if (type is "nvarchar" or "nchar" && length > 0)
                    {
                        type += $"({length / 2})";
                    }
                    else if (type is "varchar" or "char" or "varbinary" && length > 0)
                    {
                        type += $"({length})";
                    }
                    else if (length == -1 && type.EndsWith("char", StringComparison.Ordinal))
                    {
                        type += "(max)";
                    }

                    builder.Append(CultureInfo.InvariantCulture, $"  {reader.GetString(2)} {type}");
                    builder.Append(reader.GetBoolean(5) ? " null" : " not null");
                    if (reader.GetBoolean(6))
                    {
                        builder.Append(" PK");
                    }

                    if (!reader.IsDBNull(7))
                    {
                        builder.Append(CultureInfo.InvariantCulture, $" -> {reader.GetString(7)}");
                    }

                    if (!reader.IsDBNull(8) && reader.GetString(8).Length > 0)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $" — {ErpAgentSqlGuard.Shorten(reader.GetString(8), 160)}");
                    }

                    builder.AppendLine();
                }
            }

            if (columns == 0)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"describe {ErpAgentSqlGuard.Shorten(name, 128)}: not found (use find_tables to get exact names).");
            }
        }

        return builder.ToString();
    }

    public async Task<ErpAgentQueryResult> QueryAsync(string validatedSql, CancellationToken cancellationToken)
    {
        // Defense in depth: callers validate first, but this boundary never trusts them.
        var statement = ErpAgentSqlGuard.Validate(validatedSql);

        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadUncommitted, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = QueryTimeoutSeconds;
            command.CommandText = statement;

            var builder = new StringBuilder();
            var rows = 0;
            var truncated = false;
            var reader = await command.ExecuteReaderAsync(cancellationToken);
            try
            {
                var columnCount = Math.Min(reader.FieldCount, MaximumColumns);
                var header = string.Join(" | ", Enumerable.Range(0, columnCount)
                    .Select(index => string.IsNullOrEmpty(reader.GetName(index)) ? $"col{index + 1}" : reader.GetName(index)));
                builder.AppendLine(header);
                if (reader.FieldCount > MaximumColumns)
                {
                    truncated = true;
                }

                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rows >= MaximumRows || builder.Length >= MaximumResultCharacters)
                    {
                        truncated = true;
                        break;
                    }

                    var values = new string[columnCount];
                    for (var index = 0; index < columnCount; index++)
                    {
                        values[index] = FormatValue(reader, index, ref truncated);
                    }

                    builder.AppendLine(string.Join(" | ", values));
                    rows++;
                }
            }
            finally
            {
                if (truncated)
                {
                    // Stop the server from streaming the rest of a large result to a closing reader.
                    try
                    {
                        command.Cancel();
                    }
                    catch (Exception cancel) when (cancel is DbException or InvalidOperationException)
                    {
                    }
                }

                try
                {
                    await reader.DisposeAsync();
                }
                catch (DbException) when (truncated)
                {
                    // Closing a cancelled reader may report the cancellation; the bounded rows are already read.
                }
            }

            var summary = truncated
                ? $"{rows} rows shown (result truncated to {MaximumRows} rows / {MaximumColumns} columns / {MaximumCellCharacters} chars per cell; use TOP, WHERE or aggregates for exact totals)"
                : $"{rows} rows";
            return new ErpAgentQueryResult($"{summary}:\n{builder}", rows, truncated, null);
        }
        catch (DbException error) when (!cancellationToken.IsCancellationRequested)
        {
            if (connection.State != ConnectionState.Open)
            {
                throw;
            }

            var message = ErpAgentSqlGuard.Shorten(error.Message, 300);
            return new ErpAgentQueryResult($"SQL error: {message}", 0, false, message);
        }
        finally
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollback) when (rollback is DbException or InvalidOperationException)
            {
                // A cancelled or failed command can already have ended the transaction.
            }
        }
    }

    private static string FormatValue(DbDataReader reader, int ordinal, ref bool truncated)
    {
        if (reader.IsDBNull(ordinal))
        {
            return "NULL";
        }

        var raw = reader.GetValue(ordinal);
        var text = raw switch
        {
            byte[] bytes => $"<binary {bytes.Length} bytes>",
            DateTime value => value.ToString(value.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
            bool value => value ? "1" : "0",
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString() ?? string.Empty
        };
        text = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray())
            .Replace("|", "/", StringComparison.Ordinal)
            .Trim();
        if (text.Length > MaximumCellCharacters)
        {
            truncated = true;
            return text[..MaximumCellCharacters] + "…";
        }

        return text;
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Size = 400;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed record ErpAgentQueryRecord(
    string Purpose,
    string Sql,
    int RowCount,
    bool Truncated,
    string? Error,
    long ElapsedMilliseconds);

public sealed record ErpQueryAgentResult(
    string Answer,
    string? ProviderId,
    string Model,
    long InputTokens,
    long OutputTokens,
    int ModelTurns,
    IReadOnlyList<ErpAgentQueryRecord> Queries);

/// <summary>
/// Bounded tool loop: the model explores the schema, proposes SELECTs (validated before execution)
/// and answers from the rows. It never receives connection strings and can never write.
/// </summary>
public sealed class ErpQueryAgent(IAiGateway aiGateway)
{
    public const int MaximumModelTurns = 8;
    public const int MaximumQueries = 5;
    public const int MaximumObservationCharacters = 60_000;

    private static readonly Regex JsonFence =
        new(@"^```(?:json)?\s*(?<body>[\s\S]*?)\s*```$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ErpQueryAgentResult> RunAsync(
        WorkDispatchEnvelope envelope,
        string question,
        PilotErpEvidence evidence,
        IErpQuerySession session,
        CancellationToken cancellationToken,
        IReadOnlyList<AiImageInput>? images = null,
        string? conversation = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        images ??= [];
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(session);
        evidence.Validate();

        var observations = new List<string>();
        var queries = new List<ErpAgentQueryRecord>();
        long inputTokens = 0;
        long outputTokens = 0;
        string? providerId = null;
        var model = string.Empty;
        var invalidReplies = 0;

        for (var turn = 1; turn <= MaximumModelTurns; turn++)
        {
            var finalTurn = turn == MaximumModelTurns;
            var request = new AiGatewayRequest(
                envelope.TenantId.ToString("N"),
                envelope.CompanyId.ToString("N"),
                envelope.TaskId.ToString("N"),
                $"pilot-ai:{envelope.TaskId:N}:{envelope.StepId:N}:t{turn}",
                AiCapability.Reasoning,
                BuildPrompt(question, evidence, observations, MaximumQueries - queries.Count, finalTurn, images.Count, conversation))
            {
                // The provider is stateless, so every turn carries the pictures again.
                Images = images
            };
            AiGatewayResponse response;
            try
            {
                response = await aiGateway.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException error)
            {
                // Same mapping as the catalog-only path: an unusable provider configuration is permanent.
                throw new AiProviderExecutionException($"AI provider is not usable: {error.Message}", false, error);
            }

            inputTokens = checked(inputTokens + response.InputTokens);
            outputTokens = checked(outputTokens + response.OutputTokens);
            providerId = response.ProviderId ?? providerId;
            model = response.Model;

            var output = response.Output?.Trim() ?? string.Empty;
            if (!TryParseAction(output, out var action))
            {
                invalidReplies++;
                if (finalTurn || invalidReplies > 1)
                {
                    return Result(output.Length == 0 ? "Không nhận được câu trả lời từ AI." : output, turn);
                }

                observations.Add("Your previous reply was not one valid JSON object with an \"action\" field. Reply again with exactly one JSON object.");
                continue;
            }

            switch (action.Name)
            {
                case "answer":
                    return Result(action.Text ?? output, turn);

                case "find_tables" when !finalTurn:
                    observations.Add(await session.FindTablesAsync(action.Items, cancellationToken).ConfigureAwait(false));
                    break;

                case "describe" when !finalTurn:
                    observations.Add(await session.DescribeTablesAsync(action.Items, cancellationToken).ConfigureAwait(false));
                    break;

                case "query" when !finalTurn:
                    observations.Add(await RunQueryAsync(action, session, queries, cancellationToken).ConfigureAwait(false));
                    break;

                default:
                    if (finalTurn)
                    {
                        return Result(action.Text ?? "Chưa đủ thông tin để trả lời trong giới hạn truy vấn cho phép.", turn);
                    }

                    observations.Add($"Unknown action '{ErpAgentSqlGuard.Shorten(action.Name, 40)}'. Use find_tables, describe, query or answer.");
                    break;
            }

            TrimObservations(observations);
        }

        return Result("Chưa đủ thông tin để trả lời trong giới hạn truy vấn cho phép.", MaximumModelTurns);

        ErpQueryAgentResult Result(string answer, int turns) =>
            new(
                string.IsNullOrWhiteSpace(answer) ? "Không nhận được câu trả lời từ AI." : answer.Trim(),
                providerId,
                model,
                inputTokens,
                outputTokens,
                turns,
                queries.ToArray());
    }

    private static async Task<string> RunQueryAsync(
        AgentAction action,
        IErpQuerySession session,
        List<ErpAgentQueryRecord> queries,
        CancellationToken cancellationToken)
    {
        var purpose = ErpAgentSqlGuard.Shorten(action.Purpose ?? string.Empty, 200);
        var sql = action.Sql?.Trim() ?? string.Empty;
        if (queries.Count >= MaximumQueries)
        {
            return $"Query budget of {MaximumQueries} is used up. Answer now with what you have.";
        }

        string statement;
        try
        {
            statement = ErpAgentSqlGuard.Validate(sql);
        }
        catch (InvalidOperationException rejected)
        {
            queries.Add(new(purpose, ErpAgentSqlGuard.Shorten(sql, ErpAgentSqlGuard.MaximumSqlLength), 0, false, "rejected: " + rejected.Message, 0));
            return $"Query rejected before execution: {rejected.Message}";
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await session.QueryAsync(statement, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        queries.Add(new(purpose, statement, result.RowCount, result.Truncated, result.Error, stopwatch.ElapsedMilliseconds));
        return $"query #{queries.Count} ({purpose}):\n{statement}\nresult: {result.Observation}";
    }

    private static void TrimObservations(List<string> observations)
    {
        var total = observations.Sum(observation => observation.Length);
        for (var index = 0; index < observations.Count - 1 && total > MaximumObservationCharacters; index++)
        {
            var observation = observations[index];
            if (observation.Length <= 600)
            {
                continue;
            }

            var shortened = observation[..500] + "\n…(older output shortened)";
            total -= observation.Length - shortened.Length;
            observations[index] = shortened;
        }
    }

    internal static bool TryParseAction(string output, out AgentAction action)
    {
        action = new AgentAction(string.Empty, null, null, null, []);
        var text = output.Trim();
        var fence = JsonFence.Match(text);
        if (fence.Success)
        {
            text = fence.Groups["body"].Value.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("action", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var items = new List<string>();
            foreach (var property in new[] { "keywords", "tables" })
            {
                if (root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    items.AddRange(list.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()!)
                        .Take(10));
                }
                else if (root.TryGetProperty(property, out var single) && single.ValueKind == JsonValueKind.String)
                {
                    items.Add(single.GetString()!);
                }
            }

            action = new AgentAction(
                name.GetString()!.Trim().ToLowerInvariant(),
                ReadString(root, "text") ?? ReadString(root, "answer"),
                ReadString(root, "sql"),
                ReadString(root, "purpose"),
                items);
            return action.Name.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string ConversationSection(string conversation) => $"""
        Earlier in this conversation with the same user (oldest first). Use it to understand what the new question refers to
        (customer, period, product, code) and reuse SQL that worked; run queries again for any number you report rather than
        copying an earlier answer, and ignore earlier turns that are unrelated to the new question:
        {conversation}

        """;

    private static string BuildPrompt(
        string question,
        PilotErpEvidence evidence,
        IReadOnlyList<string> observations,
        int remainingQueries,
        bool finalTurn,
        int imageCount = 0,
        string? conversation = null)
    {
        var tables = evidence.TopTables.Count == 0
            ? "(no non-system tables returned)"
            : string.Join("\n", evidence.TopTables.Select(table =>
                $"- {table.SchemaName}.{table.TableName} (~{table.ApproximateRowCount} rows)"));
        var history = observations.Count == 0
            ? "(nothing yet)"
            : string.Join("\n\n", observations.Select((observation, index) => $"[{index + 1}] {observation}"));
        var pictures = imageCount == 0 ? string.Empty
            : $"The user attached {imageCount} picture(s) to this question (screenshots, documents or photos from the customer). "
              + "Read them carefully first: extract the codes, names, numbers, dates and error messages they show, use those to "
              + "decide what to look up in the ERP, and say in your answer what you saw in the pictures.";
        var instruction = finalTurn
            ? "This is your last turn: reply now with {\"action\":\"answer\",\"text\":\"...\"}."
            : $"Reply with exactly one JSON object for your next step. Queries left: {Math.Max(remainingQueries, 0)}.";

        return $$"""
            You are the ERP data assistant of Minh Huy AI Office. You help staff answer questions about a
            customer's ERP database (Microsoft SQL Server, the company's in-house ERP) by reading real data.
            You have READ-ONLY access through these actions, one per reply, as a single JSON object:
              {"action":"find_tables","keywords":["tinh","province"]}   search table/view/column names and descriptions (max 5 keywords)
              {"action":"describe","tables":["dbo.TableName"]}          list columns, types, keys of up to 5 tables
              {"action":"query","purpose":"short reason","sql":"SELECT TOP (50) ..."}   run ONE read-only T-SQL SELECT
              {"action":"answer","text":"..."}                          final answer to the user
            Rules:
            - Only single SELECT / WITH ... SELECT statements on this database; no comments, no ';', no INTO,
              no data changes. Results are capped at 50 rows and 20 seconds, so use TOP, WHERE, GROUP BY and COUNT.
              Avoid scanning tables with millions of rows without a selective WHERE.
            - Table names may be cryptic: use find_tables (try Vietnamese without accents and English words) and
              describe before querying. Never guess column names.
            - Base every factual claim on query results; never invent values. If the data cannot answer the
              question, say what you checked and what is missing.
            - You cannot change data. If something must be fixed, explain what you found and give concrete steps
              for the user or ERP administrator; you may include a suggested fix script, clearly labelled
              "chỉ tham khảo, cần kiểm tra trước khi chạy". It is never executed by the system.
            - Answer in the same language as the question (Vietnamese by default), concise, with the key numbers
              or examples, and mention which tables you used.

            Database: {{evidence.DatabaseName}} ({{evidence.TableCount}} user tables)
            Largest tables:
            {{tables}}

            {{(string.IsNullOrWhiteSpace(conversation) ? "" : ConversationSection(conversation))}}
            User question:
            {{question}}
            {{pictures}}

            Results of your previous actions:
            {{history}}

            {{instruction}}
            """;
    }

    internal sealed record AgentAction(
        string Name,
        string? Text,
        string? Sql,
        string? Purpose,
        IReadOnlyList<string> Items);
}
