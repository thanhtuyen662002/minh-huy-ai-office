using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Opt in with an operator-provided connection that may create/drop disposable
// databases. No existing database is migrated or dropped by these tests.
public sealed class CustomerRequestSqlFactAttribute : FactAttribute
{
    public CustomerRequestSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIOFFICE_CUSTOMER_REQUEST_TEST_SQL")))
            Skip = "Set AIOFFICE_CUSTOMER_REQUEST_TEST_SQL to run the disposable real SQL Server bridge proof.";
    }
}

public sealed class CustomerRequestSqlTests
{
    [CustomerRequestSqlFact]
    public async Task RealSqlMigrationRuntimePermissionsConcurrentIngestionAndRowVersionUpdates()
    {
        using var fixture = new CustomerRequestFixture();
        await using var sql = await SqlFixture.CreateAsync(fixture);
        var body = CustomerRequestContractTests.Bytes(CustomerRequestContractTests.Sample() with { OpenQuestions = ["Which version?"] });
        // Multiple contexts/connections exercise actual SQL locks and unique indexes.
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = sql.Runtime();
            return await fixture.Ingress(db).AcceptAsync(fixture.Sign(body), body);
        }));
        Assert.Single(duplicates.Distinct()); Assert.Equal("YC-0001", duplicates[0].Code); Assert.Equal("needs_clarification", duplicates[0].Status);
        var distinct = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            var bytes = CustomerRequestContractTests.Bytes(CustomerRequestContractTests.Sample() with { IdempotencyKey = "sql-" + index });
            await using var db = sql.Runtime(); return await fixture.Ingress(db).AcceptAsync(fixture.Sign(bytes), bytes);
        }));
        Assert.Equal(new[] { "YC-0002", "YC-0003", "YC-0004", "YC-0005" }, distinct.Select(x => x.Code).Order());
        await using var read = sql.Runtime(); var portal = fixture.Portal(read);
        var detail = await portal.GetAsync(fixture.Authority, fixture.Authority.CompanyId, "YC-0001");
        Assert.Single(detail.OpenQuestions); Assert.Single(detail.Evidence); Assert.Single(detail.Events);
        Assert.Equal(8, Convert.FromBase64String(detail.Request.RowVersion).Length);
        var changes = await Task.WhenAll(new[] { "in_progress", "rejected" }.Select(async status =>
        {
            await using var db = sql.Runtime();
            try
            {
                await fixture.Portal(db).PatchAsync(fixture.Authority, fixture.Authority.CompanyId, "YC-0001",
                    new(detail.Request.RowVersion, status, fixture.Authority.UserId, true));
                return "updated";
            }
            catch (CustomerRequestConflictException) { return "conflict"; }
        }));
        Assert.Equal(1, changes.Count(x => x == "updated")); Assert.Equal(1, changes.Count(x => x == "conflict"));
        var current = await portal.GetAsync(fixture.Authority, fixture.Authority.CompanyId, "YC-0001");
        Assert.NotEqual(detail.Request.RowVersion, current.Request.RowVersion); Assert.Equal(3, current.Events.Count);
        await using (var db = sql.Runtime())
        {
            // Real database permissions preserve original AI evidence and immutable audit.
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE aioffice.CustomerRequestEvents SET ActorType=N'user'"));
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM aioffice.CustomerRequestEvents"));
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE aioffice.CustomerRequests SET Origin=N'human_confirmed'"));
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE aioffice.CustomerRequestEvidence SET Quote=N'changed'"));
        }
        // A save failure must roll back the number, nonce, ticket and children together.
        var failing = CustomerRequestContractTests.Bytes(CustomerRequestContractTests.Sample() with { IdempotencyKey = "sql-rollback" });
        var auth = fixture.Sign(failing);
        await using (var owner = sql.Owner())
            await owner.Database.ExecuteSqlRawAsync("DENY INSERT ON OBJECT::aioffice.CustomerRequestEvents TO aioffice_bridge_test;");
        await using (var db = sql.Runtime())
            await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Ingress(db).AcceptAsync(auth, failing));
        await using (var owner = sql.Owner())
        {
            Assert.Equal(5, await owner.CustomerRequests.CountAsync());
            Assert.False(await owner.CustomerRequestNonces.AnyAsync(x => x.Nonce == auth.Nonce));
            Assert.Equal(5, (await owner.CustomerRequestCounters.SingleAsync()).LastNumber);
            await owner.Database.ExecuteSqlRawAsync("REVOKE INSERT ON OBJECT::aioffice.CustomerRequestEvents FROM aioffice_bridge_test;");
        }
        await using (var db = sql.Runtime()) Assert.Equal("YC-0006", (await fixture.Ingress(db).AcceptAsync(auth, failing)).Code);
        await using (var db = sql.Runtime())
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Ingress(db).AcceptAsync(auth, failing));
    }

    private sealed class SqlFixture(string masterConnection, string databaseName, string databaseConnection) : IAsyncDisposable
    {
        internal PlatformDbContext Owner() => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(databaseConnection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "aioffice")).Options);
        internal PlatformDbContext Runtime() => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(databaseConnection).AddInterceptors(new RuntimeIdentity()).Options);

        internal static async Task<SqlFixture> CreateAsync(CustomerRequestFixture fixture)
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AIOFFICE_CUSTOMER_REQUEST_TEST_SQL")!)
            { InitialCatalog = "master", Pooling = false };
            var name = "AIOffice_CustomerBridge_Test_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            await Execute(connection.ConnectionString, $"CREATE DATABASE [{name}]");
            var master = connection.ConnectionString; connection.InitialCatalog = name;
            var owned = new SqlFixture(master, name, connection.ConnectionString);
            try
            {
                await using var db = owned.Owner(); await db.Database.MigrateAsync();
                MemberDirectoryFixture.Seed(db, fixture.Authority, role: null); await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE USER aioffice_bridge_test WITHOUT LOGIN;
                    ALTER ROLE aioffice_binding_runtime ADD MEMBER aioffice_bridge_test;
                    GRANT SELECT ON OBJECT::aioffice.Companies TO aioffice_bridge_test;
                    GRANT SELECT ON OBJECT::aioffice.Users TO aioffice_bridge_test;
                    GRANT SELECT ON OBJECT::aioffice.CompanyMemberships TO aioffice_bridge_test;
                    GRANT SELECT ON OBJECT::aioffice.RoleAssignments TO aioffice_bridge_test;
                    """);
                return owned;
            }
            catch { await owned.DisposeAsync(); throw; }
        }

        private static async Task Execute(string connectionString, string text)
        {
            await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = text; command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            // Only the exact cryptographically named database created above is removed.
            if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^AIOffice_CustomerBridge_Test_[A-F0-9]{24}$"))
                throw new InvalidOperationException("Invalid owned SQL fixture name.");
            await Execute(masterConnection, $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
        }
    }

    private sealed class RuntimeIdentity : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand(); command.CommandText = "EXECUTE AS USER=N'aioffice_bridge_test';";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
