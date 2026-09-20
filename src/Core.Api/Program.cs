using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Platform.Observability;
using MinhHuy.AIOffice.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
var deploymentEnvironment = DeploymentEnvironment.Parse(builder.Environment.EnvironmentName);

builder.Services.AddAiOfficeObservability(
    builder.Configuration,
    "MinhHuy.AIOffice.Core.Api",
    includeAspNetCoreInstrumentation: true);
builder.Logging.AddAiOfficeOpenTelemetryLogging(
    builder.Configuration,
    "MinhHuy.AIOffice.Core.Api");

string? platformConnectionString = null;
var platformConnectionSecretReference =
    builder.Configuration["AIOffice:PlatformDatabase:ConnectionSecretRef"];

if (!string.IsNullOrWhiteSpace(platformConnectionSecretReference))
{
    var secretResolver = new CompositeSecretResolver(
        new ISecretResolver[] { new EnvironmentVariableSecretResolver() });

    platformConnectionString = await secretResolver.ResolveAsync(
        SecretReference.Parse(platformConnectionSecretReference));
}

builder.Services.AddHealthChecks();
builder.Services.AddPlatformPersistence(platformConnectionString);
builder.Services.AddScoped<IRequestAuthorizationContextAccessor, RequestAuthorizationContextAccessor>();

var authority = builder.Configuration["AIOffice:Authentication:Authority"];
var audience = builder.Configuration["AIOffice:Authentication:Audience"];
var authenticationConfigured = !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(audience);
if (authenticationConfigured)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = audience;
            options.RequireHttpsMetadata = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                NameClaimType = AuthenticationClaimTypes.Subject
            };
        });
    builder.Services.AddAuthorization();
}

var app = builder.Build();

var correlationLogger = app.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("MinhHuy.AIOffice.RequestCorrelation");

app.Use(async (httpContext, next) =>
{
    var taskId = httpContext.Request.Headers[TelemetryHeaders.TaskId].FirstOrDefault();
    var companyId = httpContext.Request.Headers[TelemetryHeaders.CompanyId].FirstOrDefault();

    if (!TelemetryCorrelationContext.TryCreate(
            taskId,
            companyId,
            System.Diagnostics.Activity.Current,
            out var correlation))
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new
        {
            error = "Invalid observability correlation header."
        });
        return;
    }

    correlation.ApplyTo(System.Diagnostics.Activity.Current);
    using var scope = correlationLogger.BeginScope(correlation.ToLogScope());
    var started = System.Diagnostics.Stopwatch.GetTimestamp();

    try
    {
        await next();
    }
    finally
    {
        AiOfficeTelemetry.RecordHttpRequest(
            httpContext.Response.StatusCode,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }
});

if (authenticationConfigured)
{
    app.UseAuthentication();
    app.UseMiddleware<RequestAuthorizationContextMiddleware>();
    app.UseAuthorization();
}

app.MapGet("/", () => Results.Ok(new
{
    service = ProjectInfo.ProductName,
    component = "Core.Api",
    environment = deploymentEnvironment.ToString(),
    status = "ok"
}));

app.MapHealthChecks("/health");

if (authenticationConfigured)
{
    app.MapGet("/api/auth/context", (IRequestAuthorizationContextAccessor accessor) =>
    {
        var current = accessor.Current;
        return current is null
            ? Results.Forbid()
            : Results.Ok(new
            {
                current.Context.TenantId,
                current.Context.CompanyId,
                current.Context.UserId,
                current.Roles
            });
    }).RequireAuthorization();
}
else
{
    app.MapGet("/api/auth/context", () => Results.Json(
        new { error = "Authentication is not configured." },
        statusCode: StatusCodes.Status503ServiceUnavailable));
}

app.Run();

public partial class Program;
