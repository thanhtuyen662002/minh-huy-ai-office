using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Platform.Observability;
using MinhHuy.AIOffice.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
var deploymentEnvironment = DeploymentEnvironment.Parse(builder.Environment.EnvironmentName);

builder.Services.AddAiOfficeObservability(builder.Configuration, "MinhHuy.AIOffice.Core.Api", includeAspNetCoreInstrumentation: true);
builder.Logging.AddAiOfficeOpenTelemetryLogging(builder.Configuration, "MinhHuy.AIOffice.Core.Api");

string? platformConnectionString = null;
var platformConnectionSecretReference = builder.Configuration["AIOffice:PlatformDatabase:ConnectionSecretRef"];
var secretResolver = new CompositeSecretResolver(new ISecretResolver[] { new EnvironmentVariableSecretResolver() });
if (!string.IsNullOrWhiteSpace(platformConnectionSecretReference))
{
    platformConnectionString = await secretResolver.ResolveAsync(SecretReference.Parse(platformConnectionSecretReference));
}

builder.Services.AddHealthChecks();
builder.Services.AddPlatformPersistence(platformConnectionString);
builder.Services.AddSingleton(secretResolver);
builder.Services.AddScoped<DataSourceRegistryService>();
builder.Services.AddScoped<IDataSourceConnectionProbe, SqlDataSourceConnectionProbe>();
builder.Services.AddScoped<DataSourceConnectionTestService>();
builder.Services.AddScoped<IRequestAuthorizationContextAccessor, RequestAuthorizationContextAccessor>();

var authority = builder.Configuration["AIOffice:Authentication:Authority"];
var audience = builder.Configuration["AIOffice:Authentication:Audience"];
var authenticationConfigured = !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(audience);
if (authenticationConfigured)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true, NameClaimType = AuthenticationClaimTypes.Subject };
    });
    builder.Services.AddAuthorization();
}

var app = builder.Build();
var correlationLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MinhHuy.AIOffice.RequestCorrelation");
app.Use(async (httpContext, next) =>
{
    var taskId = httpContext.Request.Headers[TelemetryHeaders.TaskId].FirstOrDefault();
    var companyId = httpContext.Request.Headers[TelemetryHeaders.CompanyId].FirstOrDefault();
    if (!TelemetryCorrelationContext.TryCreate(taskId, companyId, System.Diagnostics.Activity.Current, out var correlation))
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid observability correlation header." });
        return;
    }
    correlation.ApplyTo(System.Diagnostics.Activity.Current);
    using var scope = correlationLogger.BeginScope(correlation.ToLogScope());
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    try { await next(); }
    finally { AiOfficeTelemetry.RecordHttpRequest(httpContext.Response.StatusCode, System.Diagnostics.Stopwatch.GetElapsedTime(started)); }
});

if (authenticationConfigured)
{
    app.UseAuthentication();
    app.UseMiddleware<RequestAuthorizationContextMiddleware>();
    app.UseAuthorization();
}

static AuthorizationContext? AuthorizedContext(IRequestAuthorizationContextAccessor accessor) => accessor.Current?.Context;
static IResult AuthenticationUnavailable() => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Authentication is not configured.");

app.MapGet("/", () => Results.Ok(new { service = ProjectInfo.ProductName, component = "Core.Api", environment = deploymentEnvironment.ToString(), status = "ok" }));
app.MapHealthChecks("/health");

var dataSources = app.MapGroup("/api/data-sources");
dataSources.MapGet("/", async (IRequestAuthorizationContextAccessor accessor, DataSourceRegistryService registry, CancellationToken cancellationToken) =>
{
    if (!authenticationConfigured) return AuthenticationUnavailable();
    var context = AuthorizedContext(accessor);
    return context is null ? Results.Forbid() : Results.Ok(await registry.ListAsync(context, cancellationToken));
});
dataSources.MapPost("/", async (IRequestAuthorizationContextAccessor accessor, DataSourceRegistryService registry, DataSourceRegistryWriteRequest request, CancellationToken cancellationToken) =>
{
    if (!authenticationConfigured) return AuthenticationUnavailable();
    var context = AuthorizedContext(accessor);
    if (context is null) return Results.Forbid();
    var created = await registry.CreateAsync(context, request, cancellationToken);
    return Results.Created($"/api/data-sources/{created.DataSourceId}", created);
});
dataSources.MapPut("/{dataSourceId:guid}", async (Guid dataSourceId, IRequestAuthorizationContextAccessor accessor, DataSourceRegistryService registry, DataSourceRegistryWriteRequest request, CancellationToken cancellationToken) =>
{
    if (!authenticationConfigured) return AuthenticationUnavailable();
    var context = AuthorizedContext(accessor);
    if (context is null) return Results.Forbid();
    var updated = await registry.UpdateAsync(context, dataSourceId, request, cancellationToken);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});
dataSources.MapPost("/{dataSourceId:guid}/connection-test", async (Guid dataSourceId, IRequestAuthorizationContextAccessor accessor, DataSourceConnectionTestService tester, CancellationToken cancellationToken) =>
{
    if (!authenticationConfigured) return AuthenticationUnavailable();
    var context = AuthorizedContext(accessor);
    if (context is null) return Results.Forbid();
    var result = await tester.TestAsync(context, dataSourceId, cancellationToken);
    return result.Status == DataSourceConnectionTestStatus.NotAuthorized ? Results.Forbid() : Results.Ok(result);
});

app.Run();
public partial class Program;
