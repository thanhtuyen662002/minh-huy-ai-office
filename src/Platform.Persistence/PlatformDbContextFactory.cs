using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("AIOFFICE_DB_CONNECTION");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set AIOFFICE_DB_CONNECTION before running EF Core design-time commands.");
        }

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.DefaultSchema))
            .Options;

        return new PlatformDbContext(options);
    }
}
