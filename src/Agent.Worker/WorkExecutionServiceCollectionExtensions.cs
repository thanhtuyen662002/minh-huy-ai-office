using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Agent.Worker;

public static class WorkExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the durable RabbitMQ execution pipeline. The caller must provide the authoritative
    /// step executor and platform database configuration; this deliberately has no placeholder
    /// executor or implicit connection string so the hosted consumer cannot start in an unsafe mode.
    /// </summary>
    public static IServiceCollection AddDurableRabbitMqWorkExecution<TExecutor>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase,
        Action<RabbitMqWorkOptions> configureRabbitMq)
        where TExecutor : class, IWorkStepExecutor
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDatabase);
        ArgumentNullException.ThrowIfNull(configureRabbitMq);

        services.AddDbContext<PlatformDbContext>(configureDatabase);
        services.AddScoped<IWorkStepExecutor, TExecutor>();
        services.AddScoped<IWorkDeliveryHandler, PersistentWorkDeliveryHandler>();
        services.AddOptions<RabbitMqWorkOptions>()
            .Configure(configureRabbitMq)
            .Validate(options => IsValid(options), "RabbitMQ work options are invalid.")
            .ValidateOnStart();
        services.AddSingleton<IWorkPublisher, RabbitMqWorkPublisher>();
        services.AddHostedService<RabbitMqWorkConsumer>();
        return services;
    }

    private static bool IsValid(RabbitMqWorkOptions options)
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
