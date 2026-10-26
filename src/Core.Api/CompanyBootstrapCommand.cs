using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

internal static class CompanyBootstrapCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var names = new[] { "--tenant-id", "--company-id", "--company-code", "--company-name", "--admin-email" };
            if (args.Length != 11 || args[0] != "--bootstrap-company") throw new ArgumentException();
            for (var index = 1; index < args.Length; index += 2)
                if (!names.Contains(args[index], StringComparer.Ordinal) || !values.TryAdd(args[index], args[index + 1])) throw new ArgumentException();
            var connection = Environment.GetEnvironmentVariable("AIOFFICE_DB_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException();
            await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.DefaultSchema)).Options);
            // Migrations are an explicit deployment step, never an implicit CLI side effect.
            var result = await new CompanyInvitationService(database, new EfAuthorizationDirectory(database)).BootstrapAsync(
                Guid.Parse(values["--tenant-id"]), Guid.Parse(values["--company-id"]), values["--company-code"], values["--company-name"], values["--admin-email"]);
            Console.WriteLine($"Company bootstrap recorded. Invitation {result.Id:D}; expires {result.ExpiresAtUtc:O}.");
            return 0;
        }
        catch
        {
            // Provider exceptions may contain the connection string or SQL values.
            Console.Error.WriteLine("Company bootstrap failed. Check arguments, database access, migrations and existing bootstrap state.");
            return 1;
        }
    }
}
