using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Platform.Persistence;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Agent.Worker;

public static class WorkExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the durable RabbitMQ execution pipeline with mandatory prompt-independent tool
    /// authorization and immutable audit. The raw executor is never exposed as IWorkStepExecutor;
    /// the delivery handler can resolve only the authorized decorator.
    /// </summary>
    public static IServiceCollection AddDurableRabbitMqWorkExecution<TExecutor, TMetadataProvider, TPermissionProvider>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase,
        Action<RabbitMqWorkOptions> configureRabbitMq)
        where TExecutor : class, IRawWorkStepExecutor
        where TMetadataProvider : class, ITrustedToolExecutionMetadataProvider
        where TPermissionProvider : class, IToolPermissionProvider
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDatabase);
        ArgumentNullException.ThrowIfNull(configureRabbitMq);

        services.AddDbContext<PlatformDbContext>(configureDatabase);

        // Keep the side-effecting executor behind an internal marker. All worker delivery resolves
        // IWorkStepExecutor to the authorization/audit decorator, so prompts and broker payloads
        // cannot bypass policy enforcement by selecting the raw implementation directly.
        services.AddScoped<TExecutor>();
        services.AddScoped<IRawWorkStepExecutor>(provider => provider.GetRequiredService<TExecutor>());
        services.AddScoped<ITrustedToolExecutionMetadataProvider, TMetadataProvider>();
        services.AddScoped<IToolPermissionProvider, TPermissionProvider>();
        services.AddScoped<ToolAuthorizationPolicy>();
        services.AddScoped<TrustedToolAuthorizationRequestFactory>();
        services.AddScoped<IToolExecutionAuditSink, SqlToolExecutionAuditSink>();
        services.AddScoped<ToolExecutionAuditService>();
        services.AddScoped<AuthorizedToolExecutionGate>();
        services.AddScoped<IWorkStepExecutor, AuthorizedWorkStepExecutor>();
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
