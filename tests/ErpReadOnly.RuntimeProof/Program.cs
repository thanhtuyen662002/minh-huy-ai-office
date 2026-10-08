extern alias RuntimeWorker;

using System.Data.Common;
using Microsoft.Data.SqlClient;
using MinhHuy.AiOffice.Shared.Contracts.Erp;
using MinhHuy.AIOffice.Platform.Persistence;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;

// This executable is built only by the owned SQL proof. It is not an application
// endpoint and is not included in any shipping API/worker image.
var stage = "owned-guard";
string? fixture = null;
string? operatorConnection = null;
try
{
    if (Environment.GetEnvironmentVariable("CI") != "true"
        || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
        || Environment.GetEnvironmentVariable("AIOFFICE_OWNED_ERP_PROOF") != "true") throw new InvalidOperationException();
    string Connection(string key, string user)
    {
        var value = Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException();
        var parsed = new SqlConnectionStringBuilder(value);
        if (parsed.DataSource != "sql" || parsed.InitialCatalog != "AIOfficeSample" || parsed.UserID != user
            || parsed.IntegratedSecurity || parsed.ConnectTimeout > 15) throw new InvalidOperationException();
        return value;
    }
    operatorConnection = Connection("AIOFFICE_ERP_PROOF_OPERATOR", "sa");
    var readerConnection = Connection("AIOFFICE_ERP_PROOF_READER", "aioffice_reader");
    fixture = "dbo.ReadCredentialRuntime_" + Guid.NewGuid().ToString("N");

    async Task<object?> SqlAsync(string text, SqlConnection? existing = null)
    {
        await using var owned = existing is null ? new SqlConnection(operatorConnection) : null;
        var connection = existing ?? owned!;
        if (owned is not null) await owned.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = text;
        return await command.ExecuteScalarAsync();
    }
    async Task DeniedAsync(Func<Task> read)
    {
        try { await read(); }
        catch (ErpReadOnlyCredentialsException error) when (error.InnerException is null) { return; }
        throw new InvalidOperationException("Expected credential denial.");
    }
    var factory = new ReaderFactory();
    var definition = new ErpReadCapabilityDefinition("sample.runtime", "1", $"SELECT Label AS label FROM {fixture}", [], new());
    var request = new ErpReadCapabilityRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        definition.Key, definition.Version, new Dictionary<string, ErpReadParameterValue>());
    var executor = new SqlServerErpReadCapabilityExecutor(factory, new ErpReadCapabilityRegistry([definition]));
    async Task AllDeniedAsync(string connection)
    {
        await DeniedAsync(async () => await new SqlDataSourceConnectionProbe(factory).ProbeAsync(connection, true));
        await DeniedAsync(async () => await new SqlServerPilotErpEvidenceReader(factory).ReadAsync(connection));
        await DeniedAsync(async () => await executor.ExecuteAsync(connection, request));
        await DeniedAsync(async () => await new SqlServerSchemaDiscovery(factory).DiscoverAsync("owned", "owned", "owned", 1, connection));
    }

    stage = "actual-positive-fixture";
    await SqlAsync($"CREATE TABLE {fixture}(Id int NOT NULL PRIMARY KEY, Label nvarchar(100) NOT NULL); INSERT {fixture} VALUES(1,N'Owned runtime value');");
    stage = "actual-positive-probe";
    await new SqlDataSourceConnectionProbe(factory).ProbeAsync(readerConnection, true);
    stage = "actual-positive-catalog";
    var catalog = await new SqlServerPilotErpEvidenceReader(factory).ReadAsync(readerConnection);
    stage = "actual-positive-catalog-result";
    if (catalog.TableCount < 1) throw new InvalidOperationException();
    stage = "actual-positive-capability";
    var evidence = await executor.ExecuteAsync(readerConnection, request);
    stage = "actual-positive-capability-result";
    if (evidence.Rows.Count != 1) throw new InvalidOperationException();
    stage = "actual-positive-schema";
    var schema = await new SqlServerSchemaDiscovery(factory).DiscoverAsync("owned", "owned", "owned", 1, readerConnection);
    stage = "actual-positive-schema-result";
    if (schema.Objects.Count == 0) throw new InvalidOperationException();
    Console.WriteLine("PASS actual qualified probe/catalog/capability/schema read paths");

    stage = "actual-elevated-replacement-session";
    await AllDeniedAsync(operatorConnection);
    Console.WriteLine("PASS actual replacement elevated credential denied independently by every read path");

    stage = "actual-mid-read-final-proof";
    await using (var connection = new SqlConnection(readerConnection))
    {
        await connection.OpenAsync();
        var readCount = 0;
        try
        {
            await DeniedAsync(async () => await ErpReadOnlyConnectionVerifier.ReadAsync(connection, async () =>
            {
                var materialized = await SqlAsync($"SELECT Label FROM {fixture} WHERE Id=1;", connection);
                readCount++;
                await SqlAsync($"GRANT UPDATE ON OBJECT::{fixture} TO aioffice_reader;");
                // The grant really permits mutation on this same open session;
                // prove it and roll back before the final qualification runs.
                var allowed = await SqlAsync($"BEGIN TRANSACTION; UPDATE {fixture} SET Label=N'Mutated'; ROLLBACK TRANSACTION; SELECT N'ALLOWED';", connection);
                if (!Equals(allowed, "ALLOWED")) throw new InvalidOperationException();
                return materialized;
            }));
            if (readCount != 1) throw new InvalidOperationException();
        }
        finally { await SqlAsync($"REVOKE UPDATE ON OBJECT::{fixture} FROM aioffice_reader;"); }
        var restored = await ErpReadOnlyConnectionVerifier.ReadAsync(connection, async () =>
            await SqlAsync($"SELECT Label FROM {fixture} WHERE Id=1;", connection));
        if (!Equals(restored, "Owned runtime value")) throw new InvalidOperationException();
    }
    Console.WriteLine("PASS actual same-session mid-read elevation discards materialized evidence and restores positive profile");

    stage = "actual-hidden-metadata";
    try
    {
        await SqlAsync("DENY VIEW DEFINITION TO aioffice_reader;");
        await AllDeniedAsync(readerConnection);
    }
    finally { await SqlAsync("GRANT VIEW DEFINITION TO aioffice_reader;"); }
    await new SqlDataSourceConnectionProbe(factory).ProbeAsync(readerConnection, true);
    Console.WriteLine("PASS actual missing metadata visibility denied by every read path and explicit owned restore");

    stage = "actual-hidden-module-through-view";
    var function = fixture.Replace("ReadCredentialRuntime_", "ReadHiddenFunction_", StringComparison.Ordinal);
    var view = fixture.Replace("ReadCredentialRuntime_", "ReadHiddenView_", StringComparison.Ordinal);
    try
    {
        await SqlAsync($"EXEC(N'CREATE FUNCTION {function}() RETURNS int AS BEGIN RETURN 1; END');");
        await SqlAsync($"EXEC(N'CREATE VIEW {view} AS SELECT {function}() AS Value;');");
        await SqlAsync($"DENY VIEW DEFINITION ON OBJECT::{function} TO aioffice_reader; DENY SELECT ON OBJECT::{function} TO aioffice_reader; DENY EXECUTE ON OBJECT::{function} TO aioffice_reader;");
        await using var reader = new SqlConnection(readerConnection);
        await reader.OpenAsync();
        // A direct module denial does not stop a same-owner view from using it.
        // The profile must reject the hidden graph before any product read.
        if (!Equals(await SqlAsync($"SELECT Value FROM {view};", reader), 1)) throw new InvalidOperationException();
        await AllDeniedAsync(readerConnection);
    }
    finally
    {
        await SqlAsync($"DROP VIEW IF EXISTS {view}; DROP FUNCTION IF EXISTS {function};");
    }
    await new SqlDataSourceConnectionProbe(factory).ProbeAsync(readerConnection, true);
    Console.WriteLine("PASS actual hidden function through accessible ownership-chain view denied before product read");
    return 0;
}
catch (Exception error)
{
    // Neither exception text nor SQL, connection values, rows or metadata are diagnostics.
    var category = error switch
    {
        SqlException sql => "sql_code_" + sql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ErpReadOnlyCredentialsException => "credential_denied",
        InvalidOperationException => "invalid_operation",
        ArgumentException => "invalid_argument",
        OperationCanceledException => "cancelled",
        _ => "other_failure"
    };
    Console.Error.WriteLine("FAIL owned ERP runtime proof at " + stage + " category " + category);
    return 1;
}
finally
{
    if (fixture is not null && operatorConnection is not null)
    {
        try
        {
            await using var connection = new SqlConnection(operatorConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = $"DROP TABLE IF EXISTS {fixture};";
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            Console.Error.WriteLine("FAIL owned ERP runtime proof cleanup");
            Environment.Exit(1);
        }
    }
}

internal sealed class ReaderFactory : ISqlConnectionFactory
{
    public DbConnection Create(string connectionString) => new SqlConnection(connectionString);
}
