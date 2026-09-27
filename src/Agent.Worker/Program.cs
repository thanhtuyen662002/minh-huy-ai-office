using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Agent.Worker;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Platform.Observability;

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
builder.Services.AddDurableRabbitMqWorkExecution<
    PilotDataSourceProbeExecutor,
    PilotDataSourceToolMetadataProvider,
    PilotDataSourceToolPermissionProvider>(
        options => options.UseSqlServer(
            platformConnectionString,
            sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory",
                PlatformDbContext.DefaultSchema)),
        options => builder.Configuration
            .GetSection(RabbitMqWorkOptions.SectionName)
            .Bind(options));

var host = builder.Build();
host.Run();
