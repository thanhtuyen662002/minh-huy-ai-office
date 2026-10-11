using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Owned test executable only. Legacy physical migration is distinct from
// authentic history/latestness and from shipping worker permission evidence.
internal static class GroupSignedCursorMigrationRuntimeProof
{
    internal const string Success = "PASS owned signed cursor legacy migration shadow authentic original eleven table closure empty version through UTC unsigned refusals exact51048 full rows history schema security unchanged valid upgrade trusted seven tuple FK repeat unchanged owned database removed no all seven mismatch DI or model";
    internal static string Phase { get; private set; } = "owned-guard";
    internal static int SnapshotOrdinal { get; private set; } = -1;

    // Fixed categories and a bounded ordinal only. Never format exception
    // messages, SQL, tags, connection strings or private input.
    internal static string FailureDiagnostic(Exception error)
    {
        var code = error switch
        {
            SqlException sql when sql.Number is >= -2 and <= 65535 => "sql-" + sql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlException => "sql-other",
            OperationCanceledException => "cancelled",
            ArgumentException => "argument",
            InvalidOperationException => "invalid-operation",
            NotSupportedException => "not-supported",
            _ => "other"
        };
        var ordinal = SnapshotOrdinal is >= 0 and < GroupShadowCursorSql.SourceSnapshotCount ? SnapshotOrdinal : -1;
        return "FAIL owned signed cursor migration diagnostic " + code + " snapshot=" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static async Task RunAsync(GroupScope scope, Guid allocationOperation, GroupExtractionWorkerBinding worker,
        DbContextOptions<PlatformDbContext> runtimeOptions, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (System.Transactions.Transaction.Current is not null) throw new InvalidOperationException();
        scope.Validate(); worker.Validate();
        Phase = "original-authentication";
        // Reuse the unchanged shipping historical reader, current authority,
        // retained-key resolver and independent complete original observer.
        await GroupFrontierRootRuntimeProof.RunAsync("frontier-root-replay", scope, allocationOperation, worker, runtimeOptions, token);
        var completion = new GroupCursorProofCompletion();
        var model = new PlatformDbContext(runtimeOptions);
        await completion.RunAsync(() => RunShadowAsync(scope, allocationOperation, worker, model, completion, token), model.DisposeAsync);
        completion.RequireClean(); token.ThrowIfCancellationRequested();
        Console.WriteLine(Success);
    }

    private static async Task RunShadowAsync(GroupScope scope, Guid allocationOperation, GroupExtractionWorkerBinding worker,
        PlatformDbContext model, GroupCursorProofCompletion completion, CancellationToken token)
    {
        var batch = await model.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == allocationOperation, token);
        if (model.Database.GetDbConnection().State != ConnectionState.Closed || model.ChangeTracker.Entries().Any()) throw new InvalidOperationException();
        Phase = "operator-configuration";
        var trusted = GroupCursorShadowGuard.RequireOperatorConnection(Environment.GetEnvironmentVariable("AIOFFICE_GROUP_CURSOR_PROOF_OPERATOR_CONNECTION"));
        var descriptor = GroupShadowCursorSql.NewShadow();
        GroupCursorShadowGuard.RequireShadowName(descriptor.Name);
        var owner = Guid.ParseExact(descriptor.Name["GroupCursorShadow_".Length..], "N");
        var sourceConnection = trusted;
        var masterConnection = GroupCursorShadowGuard.DatabaseConnection(trusted, "master");
        var shadowConnection = GroupCursorShadowGuard.DatabaseConnection(trusted, descriptor.Name);
        var identity = new GroupShadowCursorSql.RootIdentity(scope.TenantId, scope.CompanyId, scope.SourceBindingId, worker.ServiceId, batch.Id);
        Phase = "original-source-snapshot";
        var originalSource = await SnapshotAsync(completion, GroupShadowCursorSql.SourceSnapshots(model), GroupShadowCursorSql.SourceSnapshotCount, sourceConnection, token);
        int? databaseId = null;
        var createInvoked = false; var existingRefused = false; var initialAbsenceVerified = false;
        string[] previousHistory = [];
        ExceptionDispatchInfo? failure = null; string? failedPhase = null;
        try
        {
            Phase = "shadow-create";
            await WithConnectionAsync(completion, masterConnection, async inspectionConnection =>
            {
                var observed = await InspectAsync(inspectionConnection, token, traceAbsence: true);
                Phase = "shadow-absence-check";
                if (observed.Id is not null) throw new InvalidOperationException();
            });
            initialAbsenceVerified = true;
            await WithConnectionAsync(completion, masterConnection, async master =>
            {
                Phase = "shadow-create-command";
                var create = GroupCursorShadowGuard.CreateDatabase(master, descriptor.Name);
                await completion.RunAsync(async () =>
                {
                    Phase = "shadow-create-open";
                    await master.OpenAsync(token);
                    Phase = "shadow-create-execute";
                    createInvoked = true;
                    var result = await create.ExecuteScalarAsync(token);
                    Phase = "shadow-create-result";
                    databaseId = RequireCreatedDatabaseId(result);
                }, () => DisposeCommand(create));
            });
            Phase = "shadow-ownership";
            await WithConnectionAsync(completion, shadowConnection, async shadow =>
            {
                var mark = GroupCursorShadowGuard.MarkOwnership(shadow, descriptor.Name, owner);
                await completion.RunAsync(async () =>
                {
                    await shadow.OpenAsync(token); await mark.ExecuteNonQueryAsync(token);
                }, () => DisposeCommand(mark));
            });
            Phase = "predecessor-migration";
            await MigrateAsync(completion, shadowConnection, GroupCursorLegacyProbes.Predecessor, token);
            previousHistory = await HistoryAsync(completion, shadowConnection, token);
            if (previousHistory.Length == 0 || previousHistory[^1] != GroupCursorLegacyProbes.Predecessor
                || previousHistory.Contains(GroupCursorLegacyProbes.Target, StringComparer.Ordinal)) throw new InvalidOperationException();
            Phase = "original-seed";
            await SeedAsync(advanceOnly: false);
            var originalShadow = await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token);
            await WithConnectionAsync(completion, shadowConnection, async legacyTemplate =>
            {
                var legacyCommands = GroupCursorLegacyProbes.LegacyConstraintRefusals(legacyTemplate, scope);
                await completion.RunAsync(async () =>
                {
                    for (var i = 0; i < legacyCommands.Length; i++)
                    {
                        Phase = i == 0 ? "legacy-version-check" : "legacy-utc-check";
                        await LegacyCheckAsync(legacyCommands[i]);
                    }
                }, ProbeCleanup(legacyCommands));
            });

            await WithConnectionAsync(completion, shadowConnection, async migrationTemplate =>
            {
                var probes = GroupCursorLegacyProbes.MigrationRefusals(migrationTemplate, scope);
                await completion.RunAsync(async () =>
                {
                    for (var i = 0; i < probes.Length; i++)
                    {
                        Phase = new[] { "empty-preflight", "version-preflight", "through-preflight", "utc-preflight" }[i];
                        await ScalarOneAsync(completion, probes[i], token);
                        await RequirePreflightRefusalAsync();
                        await WithConnectionAsync(completion, shadowConnection, async shadow =>
                        {
                            var restore = GroupCursorLegacyProbes.RestoreMatchingCursor(shadow, scope);
                            await completion.RunAsync(() => ScalarOneAsync(completion, restore, token), () => DisposeCommand(restore));
                        });
                        RequireSame(originalShadow, await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token));
                    }
                }, ProbeCleanup(probes));
            });
            Phase = "unsigned-preflight";
            await WithConnectionAsync(completion, shadowConnection, async shadow =>
            {
                var remove = GroupCursorLegacyProbes.RemoveAdvance(shadow, scope);
                await completion.RunAsync(() => ScalarOneAsync(completion, remove, token), () => DisposeCommand(remove));
            });
            await RequirePreflightRefusalAsync();
            await SeedAsync(advanceOnly: true);
            RequireSame(originalShadow, await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token));

            Phase = "matching-positive-upgrade";
            await MigrateAsync(completion, shadowConnection, GroupCursorLegacyProbes.Target, token);
            var upgradedHistory = await HistoryAsync(completion, shadowConnection, token);
            if (!upgradedHistory.SequenceEqual(previousHistory.Append(GroupCursorLegacyProbes.Target))) throw new InvalidOperationException();
            var current = Context(shadowConnection);
            await completion.RunAsync(async () =>
            {
                var catalog = await current.Database.SqlQueryRaw<int>(GroupSignedCursorRuntimeProof.CatalogSql).ToArrayAsync(token);
                if (catalog.Length != 1 || catalog[0] != 1 || current.Database.CurrentTransaction is not null
                    || current.Database.GetDbConnection().State != ConnectionState.Closed || current.ChangeTracker.Entries().Any()) throw new InvalidOperationException();
            }, current.DisposeAsync);
            var upgraded = await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token);
            RequireSame(Rows(originalShadow), Rows(upgraded));
            Phase = "positive-repeat";
            await MigrateAsync(completion, shadowConnection, GroupCursorLegacyProbes.Target, token);
            RequireSame(upgraded, await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token));
            Phase = "original-source-unchanged";
            RequireSame(originalSource, await SnapshotAsync(completion, GroupShadowCursorSql.SourceSnapshots(model), GroupShadowCursorSql.SourceSnapshotCount, sourceConnection, token));
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error); failedPhase = Phase;
            existingRefused = Phase == "shadow-create-execute" && error is SqlException { Number: 51070 };
        }
        finally
        {
            if (createInvoked)
            {
                Phase = "owned-shadow-cleanup";
                try
                {
                    // Inspect even after lost CREATE/ownership-mark replies.
                    // A fresh independent cleanup deadline reserves child time.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    (int? Id, int OwnerState) observed = default;
                    await WithConnectionAsync(completion, masterConnection, async inspectionConnection =>
                    {
                        observed = await InspectAsync(inspectionConnection, cleanup.Token);
                    });
                    var cleanupId = RequireCleanupIdentity(initialAbsenceVerified, existingRefused, databaseId, observed, failure is not null);
                    if (cleanupId.HasValue)
                    {
                        // Recovery can supply DB_ID only with the exact complete
                        // ownership token; absent/foreign tokens never grant DROP.
                        await WithConnectionAsync(completion, masterConnection, async master =>
                        {
                            var drop = GroupCursorShadowGuard.DropDatabase(master, descriptor.Name, owner, cleanupId.Value);
                            await completion.RunAsync(async () =>
                            {
                                await master.OpenAsync(cleanup.Token);
                                if (await drop.ExecuteScalarAsync(cleanup.Token) is not int value || value != 1) throw new InvalidOperationException();
                            }, () => DisposeCommand(drop));
                        });
                    }
                }
                catch (Exception error)
                {
                    if (failure is null) { failure = ExceptionDispatchInfo.Capture(error); failedPhase = Phase; }
                }
            }
        }
        Phase = failedPhase ?? "complete";
        failure?.Throw();
        completion.RequireClean();
        token.ThrowIfCancellationRequested();

        async Task<(int? Id, int OwnerState)> InspectAsync(SqlConnection master, CancellationToken inspectionToken, bool traceAbsence = false)
        {
            if (traceAbsence) Phase = "shadow-absence-command";
            var inspection = GroupCursorShadowGuard.InspectOwnership(master, descriptor.Name, owner);
            return await completion.RunAsync(async () =>
            {
                if (traceAbsence) Phase = "shadow-absence-open";
                await master.OpenAsync(inspectionToken);
                if (traceAbsence) Phase = "shadow-absence-execute";
                var reader = await inspection.ExecuteReaderAsync(inspectionToken);
                if (traceAbsence) Phase = "shadow-absence-result";
                return await completion.RunAsync(() => ReadOwnershipAsync(reader, inspectionToken), reader.DisposeAsync);
            }, () => DisposeCommand(inspection));
        }

        async Task SeedAsync(bool advanceOnly)
        {
            var plan = GroupShadowCursorSql.BuildSeedPlan(model, descriptor, identity);
            await completion.RunAsync(async () =>
            {
                await WithConnectionAsync(completion, sourceConnection, async source =>
                {
                    await source.OpenAsync(token);
                    await WithTransactionAsync(completion, source, token, async transaction =>
                    {
                        var sourceLock = new SqlCommand("""
                        DECLARE @rc int; EXEC @rc=sys.sp_getapplock @Resource=@resource,@LockMode=N'Exclusive',
                            @LockOwner=N'Transaction',@LockTimeout=5000; SELECT @rc;
                        """, source, transaction) { CommandTimeout = 30 };
                        sourceLock.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value =
                        $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}";
                        await completion.RunAsync(async () =>
                        {
                            if (await sourceLock.ExecuteScalarAsync(token) is not int held || held is not (0 or 1)) throw new InvalidOperationException();
                            foreach (var template in new[] { plan.Guard }.Concat(plan.Copies.Where(x => !advanceOnly || x.Tag == "copy:GroupTerminalAdvances")))
                            {
                                template.Command.Connection = source; template.Command.Transaction = transaction;
                                await template.Command.ExecuteNonQueryAsync(token);
                            }
                        }, () => DisposeCommand(sourceLock));
                    });
                });
            }, new[] { plan.Guard }.Concat(plan.Copies).Select<GroupShadowCursorSql.CommandTemplate, Func<ValueTask>>(
                template => () => DisposeCommand(template.Command)).ToArray());
        }

        async Task LegacyCheckAsync(SqlCommand command)
        {
            var before = await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token);
            var refused = false;
            try { await ScalarOneAsync(completion, command, token); }
            catch (SqlException error) when (error.Number == 547
                && error.Errors.Cast<SqlError>().Any(x => x.Number == 547 && x.Message.Contains("CK_GroupTerminalFrontierStates_Values", StringComparison.Ordinal)))
            { refused = true; }
            completion.RequireClean();
            if (!refused) throw new InvalidOperationException();
            RequireSame(before, await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token));
        }

        async Task RequirePreflightRefusalAsync()
        {
            var before = await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token);
            var refused = false;
            try { await MigrateAsync(completion, shadowConnection, GroupCursorLegacyProbes.Target, token); }
            catch (SqlException error) when (error.Number == 51048 && error.Errors.Count == 1
                && error.Errors[0].Number == 51048 && error.Errors[0].Message == GroupCursorLegacyProbes.PreflightMessage)
            { refused = true; }
            completion.RequireClean();
            if (!refused) throw new InvalidOperationException();
            // Fresh connection after the failed real IMigrator transaction.
            RequireSame(before, await SnapshotAsync(completion, GroupShadowCursorSql.ShadowSnapshots(model, descriptor), GroupShadowCursorSql.ShadowSnapshotCount, shadowConnection, token));
            if (!(await HistoryAsync(completion, shadowConnection, token)).SequenceEqual(previousHistory)) throw new InvalidOperationException();
            RequireSame(originalSource, await SnapshotAsync(completion, GroupShadowCursorSql.SourceSnapshots(model), GroupShadowCursorSql.SourceSnapshotCount, sourceConnection, token));
        }
    }

    private static PlatformDbContext Context(string connection)
    {
        using var unopened = new SqlConnection(connection);
        GroupCursorShadowGuard.RequireShadowConnection(unopened);
        return new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.DefaultSchema)).Options);
    }

    private static async Task MigrateAsync(GroupCursorProofCompletion completion, string connection, string target, CancellationToken token)
    {
        if (target is not (GroupCursorLegacyProbes.Predecessor or GroupCursorLegacyProbes.Target)) throw new InvalidOperationException();
        var db = Context(connection);
        await completion.RunAsync(() => db.GetService<IMigrator>().MigrateAsync(target, token), () =>
        {
            if (db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed
                || db.ChangeTracker.Entries().Any()) throw new InvalidOperationException();
            return ValueTask.CompletedTask;
        }, db.DisposeAsync);
    }

    private static async Task<string[]> HistoryAsync(GroupCursorProofCompletion completion, string connection, CancellationToken token)
    {
        var db = Context(connection);
        return await completion.RunAsync(async () =>
        {
            var history = (await db.Database.GetAppliedMigrationsAsync(token)).ToArray();
            if (!history.SequenceEqual(history.Order(StringComparer.Ordinal)) || history.Distinct(StringComparer.Ordinal).Count() != history.Length
                || db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed
                || db.ChangeTracker.Entries().Any()) throw new InvalidOperationException();
            return history;
        }, db.DisposeAsync);
    }

    private static async Task ScalarOneAsync(GroupCursorProofCompletion completion, SqlCommand command, CancellationToken token)
    {
        var connection = command.Connection ?? throw new InvalidOperationException();
        GroupCursorShadowGuard.RequireShadowConnection(connection);
        await completion.RunAsync(async () =>
        {
            await connection.OpenAsync(token);
            if (await command.ExecuteScalarAsync(token) is not int count || count != 1) throw new InvalidOperationException();
        }, () => new ValueTask(connection.CloseAsync()));
    }

    private static async Task<Dictionary<string, string>> SnapshotAsync(GroupCursorProofCompletion completion, IReadOnlyList<GroupShadowCursorSql.CommandTemplate> templates,
        int expectedCount, string connection, CancellationToken token)
    {
        return await completion.RunAsync(async () =>
        {
            if (expectedCount is not (GroupShadowCursorSql.SourceSnapshotCount or GroupShadowCursorSql.ShadowSnapshotCount)
                || templates.Count != expectedCount || templates[0].Tag != "inventory"
                || templates.Select(x => x.Tag).Distinct(StringComparer.Ordinal).Count() != templates.Count) throw new InvalidOperationException();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            SnapshotOrdinal = -1;
            await WithConnectionAsync(completion, connection, async sql =>
            {
                await sql.OpenAsync(token);
                await WithTransactionAsync(completion, sql, token, async transaction =>
                {
                    await VerifySnapshotEncodingAsync(completion, sql, transaction, token);
                    foreach (var template in templates)
                    {
                        SnapshotOrdinal = result.Count;
                        template.Command.Connection = sql; template.Command.Transaction = transaction;
                        var reader = await template.Command.ExecuteReaderAsync(token);
                        var hash = await completion.RunAsync(() => ReadSnapshotHashAsync(reader, template.Tag, token), reader.DisposeAsync);
                        result.Add(template.Tag, hash);
                    }
                });
            });
            SnapshotOrdinal = -1;
            return result;
        }, templates.Select<GroupShadowCursorSql.CommandTemplate, Func<ValueTask>>(template => () =>
        {
            template.Command.Dispose(); return ValueTask.CompletedTask;
        }).ToArray());
    }

    private static async Task VerifySnapshotEncodingAsync(GroupCursorProofCompletion completion, SqlConnection sql,
        SqlTransaction transaction, CancellationToken token)
    {
        var controls = GroupShadowCursorSql.SnapshotEncodingControls();
        await completion.RunAsync(async () =>
        {
            foreach (var control in controls)
            {
                control.Command.Connection = sql; control.Command.Transaction = transaction;
                var reader = await control.Command.ExecuteReaderAsync(token);
                var hash = await completion.RunAsync(() => ReadSnapshotHashAsync(reader, control.Tag, token), reader.DisposeAsync);
                RequireSnapshotEncodingControlResult(control.Tag, hash);
            }
        }, controls.Select<GroupShadowCursorSql.CommandTemplate, Func<ValueTask>>(control => () => DisposeCommand(control.Command)).ToArray());
    }

    internal static void RequireSnapshotEncodingControlResult(string tag, string hash)
    {
        var expectedJson = tag switch
        {
            "snapshot-control:empty" => "[]",
            "snapshot-control:null-row" => "[{\"Value\":null}]",
            _ => throw new InvalidOperationException()
        };
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(expectedJson)));
        if (!StringComparer.Ordinal.Equals(expected, hash)) throw new InvalidOperationException();
    }

    private static async Task WithConnectionAsync(GroupCursorProofCompletion completion, string connection,
        Func<SqlConnection, Task> operation)
    {
        var sql = new SqlConnection(connection);
        await completion.RunAsync(() => operation(sql), () => new ValueTask(sql.CloseAsync()), sql.DisposeAsync);
    }

    private static ValueTask DisposeCommand(SqlCommand command)
    {
        command.Dispose(); return ValueTask.CompletedTask;
    }

    internal static int RequireCreatedDatabaseId(object? result)
    {
        // Closed type patterns only: never format, convert or inspect private
        // result values. Successful validation precedes resource cleanup.
        Phase = result switch
        {
            null => "shadow-create-null",
            DBNull => "shadow-create-dbnull",
            int systemId when systemId <= 4 => "shadow-create-system-id",
            int => "shadow-create-validated",
            _ => "shadow-create-non-int"
        };
        return result is int id && id > 4 ? id : throw new InvalidOperationException();
    }

    private static Func<ValueTask>[] ProbeCleanup(SqlCommand[] commands) => commands.SelectMany(command => new Func<ValueTask>[]
    {
        () => command.Connection is { } connection ? connection.DisposeAsync() : ValueTask.CompletedTask,
        () => DisposeCommand(command)
    }).ToArray();

    internal static int? RequireCleanupIdentity(bool initialAbsenceVerified, bool existingRefused, int? createdId,
        (int? Id, int OwnerState) observed, bool failed)
    {
        if (!initialAbsenceVerified || existingRefused || createdId is <= 4 || observed.Id is <= 4 || observed.OwnerState is < -1 or > 1)
            throw new InvalidOperationException();
        if (observed.Id is null)
        {
            if (!failed || observed.OwnerState != 0) throw new InvalidOperationException();
            return null;
        }
        if (observed.OwnerState != 1 || createdId.HasValue && createdId != observed.Id) throw new InvalidOperationException();
        return observed.Id;
    }

    private static async Task WithTransactionAsync(GroupCursorProofCompletion completion, SqlConnection sql, CancellationToken token,
        Func<SqlTransaction, Task> operation)
    {
        var transaction = (SqlTransaction)await sql.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var committed = false;
        await completion.RunAsync(async () =>
        {
            await operation(transaction);
            await transaction.CommitAsync(token); committed = true;
        }, () => committed ? ValueTask.CompletedTask : new ValueTask(transaction.RollbackAsync(CancellationToken.None)), transaction.DisposeAsync);
    }

    internal static async Task<string> ReadSnapshotHashAsync(DbDataReader reader, string expectedTag, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (reader.FieldCount != 2 || !await reader.ReadAsync(token) || reader.IsDBNull(0) || reader.IsDBNull(1)
            || reader.GetString(0) != expectedTag || !Regex.IsMatch(reader.GetString(1), "\\A[0-9A-F]{64}\\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException();
        var hash = reader.GetString(1);
        if (await reader.ReadAsync(token) || await reader.NextResultAsync(token)) throw new InvalidOperationException();
        return hash;
    }

    internal static async Task<(int? Id, int OwnerState)> ReadOwnershipAsync(DbDataReader reader, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (reader.FieldCount != 2 || !await reader.ReadAsync(token) || reader.IsDBNull(1)) throw new InvalidOperationException();
        int? id = reader.IsDBNull(0) ? null : reader.GetInt32(0);
        var state = reader.GetInt32(1);
        if (id is <= 4 || state is < -1 or > 1 || id is null && state != 0
            || await reader.ReadAsync(token) || await reader.NextResultAsync(token)) throw new InvalidOperationException();
        return (id, state);
    }

    private static Dictionary<string, string> Rows(Dictionary<string, string> snapshot) => snapshot
        .Where(x => x.Key.StartsWith("rows:", StringComparison.Ordinal) && x.Key != "rows:__EFMigrationsHistory")
        .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private static void RequireSame(Dictionary<string, string> original, Dictionary<string, string> current)
    {
        if (original.Count != current.Count || original.Any(x => !current.TryGetValue(x.Key, out var hash) || hash != x.Value))
            throw new InvalidOperationException();
    }
}

// Every actual resource scope uses this same first-error rule. A preserved
// expected SQL refusal is insufficient when any independent cleanup failed.
internal sealed class GroupCursorProofCompletion
{
    private ExceptionDispatchInfo? failedCleanupQualification;

    internal void RequireClean()
    {
        failedCleanupQualification?.Throw();
    }

    internal Task RunAsync(Func<Task> operation, params Func<ValueTask>[] cleanup) =>
        RunAsync(async () => { await operation(); return true; }, cleanup);

    internal async Task<T> RunAsync<T>(Func<Task<T>> operation, params Func<ValueTask>[] cleanup)
    {
        ExceptionDispatchInfo? first = null;
        T? result = default;
        try { result = await operation(); }
        catch (Exception error) { first = ExceptionDispatchInfo.Capture(error); }
        foreach (var action in cleanup)
        {
            try { await action(); }
            catch (Exception error)
            {
                first ??= ExceptionDispatchInfo.Capture(error);
                failedCleanupQualification ??= first;
            }
        }
        first?.Throw();
        return result!;
    }
}
