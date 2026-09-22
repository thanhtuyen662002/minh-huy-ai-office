using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Platform.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformPersistence(
        this IServiceCollection services,
        string? connectionString)
    {
        services.AddSingleton<ISqlConnectionFactory, SqlServerConnectionFactory>();
        services.AddSingleton<ToolAuthorizationPolicy>();

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<PlatformDbContext>(options =>
                options.UseSqlServer(connectionString, sql =>
                    sql.MigrationsHistoryTable(
                        "__EFMigrationsHistory",
                        PlatformDbContext.DefaultSchema)));
            services.AddScoped<IAuthorizationDirectory, EfAuthorizationDirectory>();
            services.AddScoped<IAuthenticatedAuthorizationDirectory, EfAuthenticatedAuthorizationDirectory>();
            services.AddScoped<IToolExecutionAuditSink, SqlToolExecutionAuditSink>();
            services.AddScoped<ToolExecutionAuditService>();
            services.AddScoped<AuthorizedToolExecutionGate>();
        }

        return services;
    }
}
