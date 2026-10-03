using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Bootstrap;
using MinhHuy.AIOffice.Platform.Persistence;

try
{
    var options = BootstrapOptions.Read(Environment.GetEnvironmentVariable);
    string Connection(string database) => new SqlConnectionStringBuilder
    {
        DataSource = "sql", InitialCatalog = database, UserID = "sa", Password = options.SqlPassword,
        Encrypt = true, TrustServerCertificate = true, ConnectTimeout = 15
    }.ConnectionString;
    // A bounded retry is needed for SQL and OIDC cold starts. Never print connection strings.
    async Task WaitAsync(Func<Task> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await operation(); return; }
            catch when (attempt < 59) { await Task.Delay(TimeSpan.FromSeconds(2)); }
        }
    }
    await WaitAsync(async () => { await using var sql = new SqlConnection(Connection("master")); await sql.OpenAsync(); });
    var dbOptions = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(Connection("AIOfficeLocal"),
        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.DefaultSchema)).Options;
    await using var database = new PlatformDbContext(dbOptions);
    await database.Database.MigrateAsync();
    using var http = new HttpClient { BaseAddress = new Uri("http://identity:8080/"), Timeout = TimeSpan.FromSeconds(15) };
    string subject = "";
    await WaitAsync(async () => subject = await new LocalIdentityProvisioner(http).ProvisionAsync(options));
    await new LocalPlatformSeeder(database).SeedAsync(options, subject);

    await using var master = new SqlConnection(Connection("master"));
    await master.OpenAsync();
    // Generated secrets were validated before SQL interpolation; identifiers are fixed.
    await using var create = new SqlCommand($"""
        IF DB_ID(N'AIOfficeSample') IS NULL CREATE DATABASE AIOfficeSample;
        IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name=N'aioffice_runtime')
            CREATE LOGIN aioffice_runtime WITH PASSWORD=N'{options.RuntimePassword}';
        IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name=N'aioffice_reader')
            CREATE LOGIN aioffice_reader WITH PASSWORD=N'{options.ReaderPassword}';
        """, master);
    await create.ExecuteNonQueryAsync();
    await using var platform = new SqlConnection(Connection("AIOfficeLocal"));
    await platform.OpenAsync();
    await using var grant = new SqlCommand("""
        IF USER_ID(N'aioffice_runtime') IS NULL
        BEGIN
            CREATE USER aioffice_runtime FOR LOGIN aioffice_runtime;
            ALTER ROLE db_datareader ADD MEMBER aioffice_runtime;
            ALTER ROLE db_datawriter ADD MEMBER aioffice_runtime;
        END
        """, platform);
    await grant.ExecuteNonQueryAsync();
    await using var sample = new SqlConnection(Connection("AIOfficeSample"));
    await sample.OpenAsync();
    await using var seed = new SqlCommand("""
        IF USER_ID(N'aioffice_reader') IS NULL
        BEGIN
            CREATE USER aioffice_reader FOR LOGIN aioffice_reader;
            ALTER ROLE db_datareader ADD MEMBER aioffice_reader;
        END
        IF OBJECT_ID(N'dbo.LocalSample', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.LocalSample (Id int NOT NULL PRIMARY KEY, Name nvarchar(200) NOT NULL);
            INSERT INTO dbo.LocalSample VALUES (1, N'Local sample data');
        END
        """, sample);
    await seed.ExecuteNonQueryAsync();
    Console.WriteLine("Local SQL, identity and company bootstrap completed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Local bootstrap failed ({error.GetType().Name}). Check service readiness and protected installation state.");
    return 1;
}
