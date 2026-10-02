using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Agent.Worker;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Platform.Observability;
using MinhHuyAiOffice.Shared.Contracts;

var builder = Host.CreateApplicationBuilder(args);
_ = DeploymentEnvironment.Parse(builder.Environment.EnvironmentName);

builder.Services.AddAiOfficeObservability(
    builder.Configuration,
    "MinhHuy.AIOffice.Agent.Worker");
builder.Logging.AddAiOfficeOpenTelemetryLogging(
    builder.Configuration,
    "MinhHuy.AIOffice.Agent.Worker");

var platformConnectionString = Environment.GetEnvironmentVariable("AIOFFICE_DB_CONNECTION");
if (string.IsNullOrWhiteSpace(platformConnectionString))
{
    throw new InvalidOperationException(
        "AIOFFICE_DB_CONNECTION must be configured before the durable worker can start.");
}

builder.Services.AddSingleton(new CompositeSecretResolver(
    new ISecretResolver[] { new EnvironmentVariableSecretResolver() }));
builder.Services.AddSingleton<ISqlConnectionFactory, SqlServerConnectionFactory>();
builder.Services.AddScoped<IDataSourceConnectionProbe, SqlDataSourceConnectionProbe>();

Action<DbContextOptionsBuilder> configureDatabase = options => options.UseSqlServer(
    platformConnectionString,
    sql => sql.MigrationsHistoryTable(
        "__EFMigrationsHistory",
        PlatformDbContext.DefaultSchema));
Action<RabbitMqWorkOptions> configureRabbitMq = options => builder.Configuration
    .GetSection(RabbitMqWorkOptions.SectionName)
    .Bind(options);

var aiOptions = OpenAiCompatibleResponsesOptions.FromEnvironment();
if (aiOptions is null)
{
    builder.Services.AddDurableRabbitMqWorkExecution<
        PilotDataSourceProbeExecutor,
        PilotDataSourceToolMetadataProvider,
        PilotDataSourceToolPermissionProvider>(
            configureDatabase,
            configureRabbitMq);
}
else
{
    builder.Services.AddSingleton(aiOptions);
    builder.Services.AddSingleton(new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120)
    });
    builder.Services.AddSingleton<IAiProviderAdapter, OpenAiCompatibleResponsesAdapter>();
    builder.Services.AddSingleton<IAiGateway>(serviceProvider =>
        new ProviderNeutralAiGateway(serviceProvider.GetServices<IAiProviderAdapter>()));
    builder.Services.AddSingleton(new PilotAiRuntimeDescriptor(aiOptions.ProviderId, aiOptions.Model));
    builder.Services.AddDurableRabbitMqWorkExecution<
        PilotAiQuestionExecutor,
        PilotAiQuestionToolMetadataProvider,
        PilotAiQuestionToolPermissionProvider>(
            configureDatabase,
            configureRabbitMq);
}

var host = builder.Build();
host.Run();
