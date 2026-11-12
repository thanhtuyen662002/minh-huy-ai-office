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

builder.Services.AddProtectedDataSourceSecrets();
builder.Services.AddSingleton<ISqlConnectionFactory, SqlServerConnectionFactory>();
builder.Services.AddScoped<IDataSourceConnectionProbe, SqlDataSourceConnectionProbe>();
builder.Services.AddScoped<IPilotErpEvidenceReader, SqlServerPilotErpEvidenceReader>();
// Off by default: Trợ lý then only sees the bounded catalog. When on, it may run validated,
// rollback-only SELECTs on the read-only customer connection (row data reaches the AI provider).
if (builder.Configuration.GetValue("AIOffice:ErpQueryAgent:Enabled", false))
{
    builder.Services.AddScoped<IErpQueryAgentBackend, SqlServerErpQueryAgentBackend>();
}

Action<DbContextOptionsBuilder> configureDatabase = options => options.UseSqlServer(
    platformConnectionString,
    sql => sql.MigrationsHistoryTable(
        "__EFMigrationsHistory",
        PlatformDbContext.DefaultSchema));
Action<RabbitMqWorkOptions> configureRabbitMq = options => builder.Configuration
    .GetSection(RabbitMqWorkOptions.SectionName)
    .Bind(options);

var primaryAiOptions =
    OpenAiCompatibleResponsesOptions.FromEnvironment("AIOFFICE_AI_PRIMARY", "openai-direct")
    ?? OpenAiCompatibleResponsesOptions.FromEnvironment();
var backupAiOptions =
    OpenAiCompatibleResponsesOptions.FromEnvironment("AIOFFICE_AI_BACKUP", "external-backup");

if (primaryAiOptions is null && backupAiOptions is not null)
{
    throw new InvalidOperationException(
        "A backup AI provider cannot be configured without a primary AI provider.");
}

// "AI dùng chung" from Kết nối answers the assistant whenever the company has filled it in;
// the server-configured providers above remain the fallback.
var botSecretRef = builder.Configuration["AIOffice:BotChannels:EncryptionKeySecretRef"];
var companyAiEnabled = SecretReference.TryParse(botSecretRef ?? "", out var botSecret) && botSecret!.Provider == "env";

if (primaryAiOptions is null && !companyAiEnabled)
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
    if (backupAiOptions is not null
        && string.Equals(
            primaryAiOptions.ProviderId,
            backupAiOptions.ProviderId,
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Primary and backup AI provider identities must differ.");
    }

    var aiHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120)
    };
    IAiGateway? serverGateway = primaryAiOptions is null
        ? null
        : new OrderedFailoverAiGateway(
            new OpenAiCompatibleResponsesAdapter(aiHttpClient, primaryAiOptions),
            backupAiOptions is null ? null : new OpenAiCompatibleResponsesAdapter(aiHttpClient, backupAiOptions));

    builder.Services.AddSingleton(aiHttpClient);
    if (companyAiEnabled)
        builder.Services.AddScoped(services => new BotSecretProtector(services.GetRequiredService<CompositeSecretResolver>(), botSecretRef!));
    // Every model call is metered: budget check before, one usage ledger row after.
    builder.Services.AddScoped<AiQuestionUsageScope>();
    builder.Services.AddScoped<AiUsageRecorder>();
    builder.Services.AddSingleton<DataSourceConcurrencyLimiter>();
    builder.Services.AddScoped<IAiGateway>(services => new MeteredAiGateway(
        companyAiEnabled
            ? new CompanyAiSettingsGateway(services.GetRequiredService<PlatformDbContext>(), services.GetRequiredService<BotSecretProtector>(),
                aiHttpClient, serverGateway)
            : serverGateway!,
        services.GetRequiredService<AiQuestionUsageScope>(), services.GetRequiredService<AiUsageRecorder>(),
        services.GetRequiredService<ILogger<MeteredAiGateway>>()));
    builder.Services.AddSingleton(
        new PilotAiRuntimeDescriptor(
            primaryAiOptions?.ProviderId ?? CompanyAiSettingsGateway.ProviderId,
            primaryAiOptions?.Model ?? "company-configured"));
    builder.Services.AddDurableRabbitMqWorkExecution<
        PilotAiQuestionExecutor,
        PilotAiQuestionToolMetadataProvider,
        PilotAiQuestionToolPermissionProvider>(
            configureDatabase,
            configureRabbitMq);
}

var groupPipelineEnabled = builder.Services.AddGroupIngressReferenceConsumer(builder.Configuration, !string.IsNullOrWhiteSpace(platformConnectionString));
var groupRecoveryEnabled = builder.Services.AddGroupConnectorRecovery(builder.Configuration, builder.Environment.EnvironmentName,
    !string.IsNullOrWhiteSpace(platformConnectionString));
var host = builder.Build();
await using (var scope = host.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<BindingStorePermissionVerifier>().RequireReadOnlyAsync();
    if (groupPipelineEnabled || groupRecoveryEnabled)
        await scope.ServiceProvider.GetRequiredService<GroupIngressPermissionVerifier>().RequireSafeRuntimeAsync();
}
host.Run();
