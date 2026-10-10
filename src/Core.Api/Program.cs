using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using MinhHuy.AIOffice.Agent.Worker;
using MinhHuy.AIOffice.Core.Api;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Core.Api.Billing;
using MinhHuy.AIOffice.Core.Api.Realtime;
using MinhHuy.AIOffice.Core.Api.Sla;
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
    platformConnectionString = await secretResolver.ResolveAsync(SecretReference.Parse(platformConnectionSecretReference));

builder.Services.AddHealthChecks();
// Return bounded 400 responses for invalid JSON in every environment. Letting
// development binding errors escape clears sensitive no-store headers and
// renders serializer/stack details through DeveloperExceptionPage.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddPlatformPersistence(platformConnectionString);
builder.Services.AddSingleton(secretResolver);
var groupIngressEnabled = builder.AddGroupIngress(!string.IsNullOrWhiteSpace(platformConnectionString), secretResolver);
builder.Services.AddOptions<RabbitMqWorkOptions>()
    .Configure(options => builder.Configuration
        .GetSection(RabbitMqWorkOptions.SectionName)
        .Bind(options));
builder.Services.AddSingleton<IWorkEnvelopePublisher, RabbitMqWorkPublisher>();
if (!string.IsNullOrWhiteSpace(platformConnectionString))
{
    builder.Services.AddScoped<DataSourceSecretBindingService>(services => new(
        services.GetRequiredService<PlatformDbContext>(), services.GetRequiredService<IAuthorizationDirectory>(),
        services.GetRequiredService<BindingStorePermissionVerifier>(), platformConnectionSecretReference));
    builder.Services.AddScoped<DataSourceRegistryService>();
    builder.Services.AddScoped<IDataSourceConnectionProbe, SqlDataSourceConnectionProbe>();
    builder.Services.AddScoped<DataSourceConnectionTestService>();
    builder.Services.AddHostedService<PilotTaskDispatchOutboxHostedService>();
}
builder.Services.AddScoped<IRequestAuthorizationContextAccessor, RequestAuthorizationContextAccessor>();
builder.Services.AddScoped<CompanyBillingReader>();
builder.Services.AddSingleton<ICompanyBillingPlanSource, UnavailableCompanyBillingPlanSource>();
builder.Services.AddScoped<CustomerSlaStatusProjection>();
builder.Services.AddCustomerSlaStatusSource(!string.IsNullOrWhiteSpace(platformConnectionString));

var authority = builder.Configuration["AIOffice:Authentication:Authority"];
var audience = builder.Configuration["AIOffice:Authentication:Audience"];
var authenticationConfigured = !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(audience);
if (authenticationConfigured)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true, NameClaimType = AuthenticationClaimTypes.Subject };
        BrowserIssuerConfiguration.Apply(options, authority!, builder.Configuration["AIOffice:Authentication:MetadataAddress"],
            builder.Environment.IsDevelopment(), builder.Configuration["AIOffice:Authentication:LocalHttp"] == "true");
    });
    builder.Services.AddAuthorization();
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<ITaskStatusPublisher, SignalRTaskStatusPublisher>();
    builder.Services.AddSingleton<ITaskWorkerRealtimeEventPublisher, TaskWorkerRealtimeSignalRBridge>();
}

var app = builder.Build();
if (!string.IsNullOrWhiteSpace(platformConnectionString))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BindingStorePermissionVerifier>().RequireReadOnlyAsync();
}
app.UseMiddleware<SensitiveResponseCacheMiddleware>();
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
app.UseMiddleware<GroupSourceRequestBoundaryMiddleware>();
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
app.MapGroupIngress(groupIngressEnabled);
app.MapGroupListener(groupIngressEnabled);
app.MapGroupEnrollment(groupIngressEnabled);
app.MapGroupSourceReads(groupIngressEnabled, authenticationConfigured);

if (authenticationConfigured)
{
    app.MapHub<TaskStatusHub>(TaskStatusRealtime.HubPath).RequireAuthorization();
    app.MapGet("/api/auth/companies", async (HttpContext httpContext,
        IServiceProvider services, CancellationToken cancellationToken) =>
    {
        var directory = services.GetService<IAuthenticatedCompanyDirectory>();
        if (directory is null) return AuthenticationUnavailable();
        var provider = httpContext.User.FindFirstValue(AuthenticationClaimTypes.IdentityProvider);
        var subject = httpContext.User.FindFirstValue(AuthenticationClaimTypes.Subject)
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (provider is null || subject is null) return (IResult)Results.Forbid();
        try { return Results.Ok(new { items = await directory.ListAsync(provider, subject, cancellationToken) }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }).WithMetadata(IdentityCompanyDirectoryEndpoint.Instance).RequireAuthorization();
    app.MapGet("/api/auth/context", (IRequestAuthorizationContextAccessor accessor) =>
    {
        var current = accessor.Current;
        return current is null ? Results.Forbid() : Results.Ok(new { current.Context.TenantId, current.Context.CompanyId, current.Context.UserId, current.Roles });
    }).RequireAuthorization();
    app.MapGet("/api/billing/plan", async (IRequestAuthorizationContextAccessor accessor, [FromServices] CompanyBillingReader reader, CancellationToken cancellationToken) =>
    {
        try { var plan = await reader.GetCurrentAsync(accessor, cancellationToken); return plan is null ? Results.NotFound() : Results.Ok(plan); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }).RequireAuthorization();
    app.MapGet("/api/sla/status", async (IRequestAuthorizationContextAccessor accessor, [FromServices] CustomerSlaStatusProjection projection, CancellationToken cancellationToken) =>
    {
        try { var status = await projection.GetCurrentAsync(accessor, cancellationToken); return status is null ? Results.NotFound() : Results.Ok(status); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }).RequireAuthorization();
    app.MapGet("/api/audit", async (IRequestAuthorizationContextAccessor accessor, [FromServices] CustomerAuditProjection projection, [FromQuery] int offset, [FromQuery] int limit, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor);
        if (context is null) return (IResult)Results.Forbid();
        try
        {
            var pageLimit = limit == 0 ? 50 : limit;
            var items = await projection.ListAsync(new CustomerAuditQuery(context.TenantId, context.CompanyId, context.UserId, offset, pageLimit), cancellationToken);
            return Results.Ok(items);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentOutOfRangeException) { return Results.BadRequest(new { error = "Invalid audit pagination." }); }
    }).RequireAuthorization();
}
else
{
    app.MapGet("/api/auth/companies", AuthenticationUnavailable);
    app.MapGet("/api/auth/context", () => Results.Json(new { error = "Authentication is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/api/billing/plan", AuthenticationUnavailable);
    app.MapGet("/api/sla/status", AuthenticationUnavailable);
    app.MapGet("/api/audit", AuthenticationUnavailable);
}

if (authenticationConfigured && !string.IsNullOrWhiteSpace(platformConnectionString))
{
    app.MapCompanyMembershipAccess();
    app.MapCompanyAdministrator();
    app.MapTaskSubmissionIntents();
    app.MapGet("/api/company/members", async (IRequestAuthorizationContextAccessor accessor,
        [FromServices] CompanyMemberDirectory directory, [FromQuery] int? offset, [FromQuery] int? limit, [FromQuery] bool? includeAccessVersion, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor);
        if (context is null) return (IResult)Results.Forbid();
        try
        {
            var page = await directory.ListAsync(context, offset ?? 0, limit ?? 50, cancellationToken);
            if (includeAccessVersion == true)
                return Results.Ok(new
                {
                    page.CompanyId,
                    Items = page.Items.Select(member => new
                    {
                        member.UserId,
                        member.DisplayName,
                        member.UserActive,
                        member.MembershipActive,
                        member.Roles,
                        member.MembershipVersion
                    }),
                    page.Offset,
                    page.Limit,
                    page.HasMore
                });
            return Results.Ok(page);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentOutOfRangeException) { return Results.BadRequest(new { error = "Invalid member pagination." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException)
        { return Results.Json(new { error = "Company directory is unavailable." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    }).RequireAuthorization();
    app.MapPost("/api/tasks", async (
        IRequestAuthorizationContextAccessor accessor,
        [FromServices] PilotTaskSubmissionService submission,
        CustomerPilotTaskRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor);
        if (context is null) return (IResult)Results.Forbid();

        try
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Results.BadRequest(new { error = "Idempotency-Key header is required." });
            }

            var accepted = await submission.SubmitAsync(context, request, idempotencyKey, cancellationToken: cancellationToken);
            return Results.Accepted($"/api/tasks/{accepted.TaskId}", accepted);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (TaskSubmissionIntentConflictException error)
        { return Results.Conflict(new { error = "Task submission conflicts with current state.", code = error.Code }); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("idempotency key", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Results.Json(new { error = "Task submission is unavailable. Reconcile the same operation." }, statusCode: 503); }
    }).RequireAuthorization();

    app.MapGet("/api/tasks", async (HttpRequest request, IRequestAuthorizationContextAccessor accessor,
        [FromServices] TaskHistoryService history, [FromQuery] int? offset, [FromQuery] int? limit, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor);
        if (context is null) return (IResult)Results.Forbid();
        if (request.Query.Any(pair => pair.Key is not ("offset" or "limit") || pair.Value.Count != 1)
            || request.Query.Any(pair => !System.Text.RegularExpressions.Regex.IsMatch(pair.Value.ToString(), "^(0|[1-9][0-9]*)$")))
            return Results.BadRequest(new { error = "Invalid task history page." });
        try { return Results.Ok(await history.ListAsync(context, offset ?? 0, limit ?? 25, cancellationToken)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid task history page." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException)
        { return Results.Json(new { error = "Task history is unavailable." }, statusCode: 503); }
    }).RequireAuthorization();

    app.MapGet("/api/tasks/{taskId:guid}/history", async (Guid taskId, HttpRequest request,
        IRequestAuthorizationContextAccessor accessor, [FromServices] TaskHistoryService history, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor);
        if (context is null) return (IResult)Results.Forbid();
        if (request.Query.Count != 0) return Results.BadRequest(new { error = "Invalid task history selector." });
        try
        {
            var detail = await history.GetAsync(context, taskId, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid task identity." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException)
        { return Results.Json(new { error = "Task history is unavailable." }, statusCode: 503); }
    }).RequireAuthorization();

    app.MapGet("/api/tasks/{taskId:guid}", async (
        Guid taskId,
        IRequestAuthorizationContextAccessor accessor,
        [FromServices] PilotTaskResultService projection,
        CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor);
        if (context is null) return (IResult)Results.Forbid();

        try
        {
            var snapshot = await projection.GetAsync(context, taskId, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }).RequireAuthorization();
}
else
{
    static IResult PilotTaskUnavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Pilot task execution is not configured.");
    app.MapPost("/api/tasks", PilotTaskUnavailable);
    app.MapUnavailableTaskSubmissionIntents();
    app.MapGet("/api/tasks", PilotTaskUnavailable);
    app.MapGet("/api/tasks/{taskId:guid}", PilotTaskUnavailable);
    app.MapGet("/api/tasks/{taskId:guid}/history", PilotTaskUnavailable);
    app.MapGet("/api/company/members", () => Results.Json(new { error = "Company directory is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/company/members/{userId:guid}/access", () => Results.Json(new { error = "Company administration is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/company/members/{userId:guid}/administrator", () => Results.Json(new { error = "Company administration is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable));
}

var dataSources = app.MapGroup("/api/data-sources");
if (authenticationConfigured)
{
    dataSources.MapGet("/registration-options", async (IRequestAuthorizationContextAccessor accessor,
        [FromServices] DataSourceRegistryService registry, [FromQuery] int? offset, [FromQuery] int? limit, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor); if (context is null) return (IResult)Results.Forbid();
        try { return Results.Ok(await registry.ListRegistrationOptionsAsync(context, offset ?? 0, limit ?? 50, cancellationToken)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid registration options page." }); }
    });
    dataSources.MapPost("/read-only-registration", async (IRequestAuthorizationContextAccessor accessor,
        [FromServices] DataSourceRegistryService registry, DataSourceReadOnlyRegistrationRequest request, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor); if (context is null) return (IResult)Results.Forbid();
        try { return Results.Ok(await registry.RegisterReadOnlyAsync(context, request, cancellationToken)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid source registration." }); }
        catch (DataSourceRegistrationConflictException) { return Results.Conflict(new { error = "Source registration conflicts with current state." }); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException) { return Results.Conflict(new { error = "Source registration could not be saved. Retry with the same operation ID." }); }
        catch (System.Data.Common.DbException) { return Results.Json(new { error = "Source registration is temporarily unavailable. Retry with the same operation ID." }, statusCode: 503); }
    });
    dataSources.MapGet("/", async (IRequestAuthorizationContextAccessor accessor, [FromServices] DataSourceRegistryService registry, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor); if (context is null) return (IResult)Results.Forbid();
        return Results.Ok(await registry.ListAsync(context, cancellationToken));
    });
    dataSources.MapPost("/", async (IRequestAuthorizationContextAccessor accessor, [FromServices] DataSourceRegistryService registry, DataSourceRegistryWriteRequest request, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor); if (context is null) return (IResult)Results.Forbid();
        try
        {
            var created = await registry.CreateAsync(context, request, cancellationToken); return Results.Created($"/api/data-sources/{created.Id}", created);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
    });
    dataSources.MapPut("/{dataSourceId:guid}", async (Guid dataSourceId, IRequestAuthorizationContextAccessor accessor, [FromServices] DataSourceRegistryService registry, DataSourceRegistryWriteRequest request, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor); if (context is null) return (IResult)Results.Forbid();
        try
        {
            var updated = await registry.UpdateAsync(context, dataSourceId, request, cancellationToken); return updated is null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
    });
    dataSources.MapPut("/{dataSourceId:guid}/metadata", async (Guid dataSourceId, IRequestAuthorizationContextAccessor accessor, [FromServices] DataSourceRegistryService registry, DataSourceMetadataWriteRequest request, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor); if (context is null) return (IResult)Results.Forbid();
        try
        {
            var updated = await registry.UpdateMetadataAsync(context, dataSourceId, request, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "Invalid source metadata." });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { error = "Source metadata conflicts with the current registry." });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Results.Conflict(new { error = "Source metadata could not be saved. Refresh the registry before retrying." });
        }
    });
    dataSources.MapPost("/{dataSourceId:guid}/connection-test", async (Guid dataSourceId, IRequestAuthorizationContextAccessor accessor, [FromServices] DataSourceConnectionTestService tester, CancellationToken cancellationToken) =>
    {
        var context = AuthorizedContext(accessor); if (context is null) return (IResult)Results.Forbid();
        var result = await tester.TestAsync(context, dataSourceId, cancellationToken); return result.Code == DataSourceConnectionTestCodes.NotAuthorized ? Results.Forbid() : Results.Ok(result);
    });
}
else
{
    dataSources.MapGet("/registration-options", AuthenticationUnavailable);
    dataSources.MapPost("/read-only-registration", AuthenticationUnavailable);
    dataSources.MapGet("/", AuthenticationUnavailable);
    dataSources.MapPost("/", AuthenticationUnavailable);
    dataSources.MapPut("/{dataSourceId:guid}", AuthenticationUnavailable);
    dataSources.MapPut("/{dataSourceId:guid}/metadata", AuthenticationUnavailable);
    dataSources.MapPost("/{dataSourceId:guid}/connection-test", AuthenticationUnavailable);
}

app.Run();
public partial class Program;
