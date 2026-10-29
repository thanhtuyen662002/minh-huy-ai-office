using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

internal static class CustomerRequestEndpoints
{
    private const string PortalPath = "/api/companies/{companyId:guid}/customer-requests";

    internal static bool AddCustomerRequests(this WebApplicationBuilder builder, bool hasDatabase)
    {
        var enabled = builder.Configuration["AIOffice:CustomerRequests:Enabled"] == "true" && hasDatabase;
        if (!enabled && !(builder.Configuration["AIOffice:BotChannels:Enabled"] == "true" && hasDatabase)) return false;
        var bindings = builder.Configuration.GetSection("AIOffice:CustomerRequests:Services").GetChildren().Select(section =>
        {
            if (!Guid.TryParse(section["ServiceId"], out var service) || !Guid.TryParse(section["TenantId"], out var tenant) ||
                !Guid.TryParse(section["CompanyId"], out var company))
                throw new InvalidOperationException("Invalid customer request service configuration.");
            return new CustomerRequestServiceBinding(service, tenant, company, section["SecretRef"] ?? "",
                section.GetSection("AllowedSourceGroupExternalIds").GetChildren().Select(x => x.Value ?? "").ToArray());
        }).ToArray();
        builder.Services.AddSingleton(new CustomerRequestServiceDirectory(bindings));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<CustomerRequestAuthenticator>();
        builder.Services.AddScoped<CustomerRequestIngress>();
        builder.Services.AddScoped<CustomerRequestDigestReader>();
        builder.Services.AddScoped<CustomerRequestStatusUpdater>();
        return enabled;
    }

    internal static void MapCustomerRequests(this WebApplication app, bool enabled, bool portalAvailable)
    {
        if (!enabled)
        {
            app.MapPost(CustomerRequestAuthenticator.Path, () => Results.NotFound());
            app.MapGet(CustomerRequestDigestReader.Path, () => Results.NotFound());
            app.MapPost(CustomerRequestStatusUpdater.Path, () => Results.NotFound());
            app.MapGet(PortalPath, () => Results.NotFound());
            app.MapGet(PortalPath + "/{code}", () => Results.NotFound());
            app.MapPatch(PortalPath + "/{code}", () => Results.NotFound());
            return;
        }
        app.MapPost(CustomerRequestAuthenticator.Path, (HttpRequest request, [FromServices] CustomerRequestIngress ingress, CancellationToken ct) =>
            Execute(async () =>
            {
                if (request.Query.Count != 0 || request.Path.Value != CustomerRequestAuthenticator.Path || request.Method != "POST") throw new ArgumentException();
                var signature = Signature(request);
                var bytes = await ReadBody(request, CustomerRequestJson.MaximumBodyBytes, ct);
                try { return Results.Ok(await ingress.AcceptAsync(signature, bytes, ct)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }));
        app.MapPost(CustomerRequestStatusUpdater.Path, (HttpRequest request, [FromServices] CustomerRequestStatusUpdater updater, CancellationToken ct) =>
            Execute(async () =>
            {
                if (request.Query.Count != 0 || request.Path.Value != CustomerRequestStatusUpdater.Path || request.Method != "POST") throw new ArgumentException();
                var signature = Signature(request);
                var bytes = await ReadBody(request, CustomerRequestJson.MaximumStatusBytes, ct);
                try { return Results.Ok(await updater.UpdateAsync(signature, bytes, ct)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }));
        app.MapGet(CustomerRequestDigestReader.Path, (HttpRequest request, [FromServices] CustomerRequestDigestReader digest, CancellationToken ct) =>
            Execute(async () =>
            {
                if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0) throw new ArgumentException();
                if (request.Query.Any(x => x.Key != "since" || x.Value.Count != 1)) throw new ArgumentException();
                long? since = request.Query.ContainsKey("since") ? long.TryParse(request.Query["since"], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    ? value : throw new ArgumentException() : null;
                // Refuse aliases/escaping so the exact signed target is unambiguous.
                if (request.Path + request.QueryString != CustomerRequestDigestReader.Target(since)) throw new ArgumentException();
                return Results.Ok(await digest.ReadAsync(Signature(request), since, ct));
            }));

        if (!portalAvailable)
        {
            app.MapGet(PortalPath, Unavailable);
            app.MapGet(PortalPath + "/{code}", Unavailable);
            app.MapPatch(PortalPath + "/{code}", Unavailable);
            return;
        }
        app.MapGet(PortalPath, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] CustomerRequestPortal portal, CancellationToken ct) => Execute(async () =>
            {
                var authority = accessor.Current?.Context;
                if (authority is null || authority.CompanyId != companyId) return Results.Forbid();
                foreach (var query in request.Query)
                    if (query.Key is not ("status" or "customer" or "offset" or "limit") || query.Value.Count != 1) throw new ArgumentException();
                var offset = Number(request, "offset", 0);
                var limit = Number(request, "limit", 50);
                return Results.Ok(await portal.ListAsync(authority, companyId, request.Query["status"].FirstOrDefault(),
                    request.Query["customer"].FirstOrDefault(), offset, limit, ct));
            })).RequireAuthorization();
        app.MapGet(PortalPath + "/{code}", (Guid companyId, string code, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] CustomerRequestPortal portal, CancellationToken ct) => Execute(async () =>
            {
                var authority = accessor.Current?.Context;
                if (authority is null || authority.CompanyId != companyId) return Results.Forbid();
                if (request.Query.Count != 0) throw new ArgumentException();
                return Results.Ok(await portal.GetAsync(authority, companyId, code, ct));
            })).RequireAuthorization();
        app.MapPatch(PortalPath + "/{code}", (Guid companyId, string code, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] CustomerRequestPortal portal, CancellationToken ct) => Execute(async () =>
            {
                var authority = accessor.Current?.Context;
                if (authority is null || authority.CompanyId != companyId) return Results.Forbid();
                if (request.Query.Count != 0) throw new ArgumentException();
                var bytes = await ReadBody(request, CustomerRequestJson.MaximumPatchBytes, ct);
                try { return Results.Ok(await portal.PatchAsync(authority, companyId, code, CustomerRequestJson.ReadPatch(bytes), ct)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            })).RequireAuthorization();
    }

    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = "Customer request access is unavailable." }, statusCode: 403); }
        catch (CustomerRequestNotFoundException) { return Results.NotFound(new { error = "Customer request is unavailable." }); }
        catch (CustomerRequestConflictException) { return Results.Conflict(new { error = "Customer request changed. Reload it before updating." }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Customer request changed. Reload it before updating." }); }
        catch (BadHttpRequestException error) { return Results.Json(new { error = "Invalid customer request." }, statusCode: error.StatusCode); }
        catch (Exception error) when (error is ArgumentException or JsonException) { return Results.BadRequest(new { error = "Invalid customer request." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or InvalidOperationException or OperationCanceledException)
        { return Unavailable(); }
    }

    private static IResult Unavailable() => Results.Json(new { error = "Customer requests are temporarily unavailable." }, statusCode: 503);

    internal static CustomerRequestSignature Signature(HttpRequest request)
    {
        string Header(string name)
        {
            var values = request.Headers[name];
            return values.Count == 1 && values[0] is string value && value.Length <= 64 ? value : throw new ArgumentException();
        }
        Guid Id(string name)
        {
            var value = Header(name);
            return Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value ? id : throw new ArgumentException();
        }
        var service = Id("X-AIOffice-Request-Service");
        var nonce = Id("X-AIOffice-Request-Nonce");
        var timestamp = Header("X-AIOffice-Request-Timestamp");
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var time) || time.ToString(CultureInfo.InvariantCulture) != timestamp) throw new ArgumentException();
        return new(service, time, nonce, Header("X-AIOffice-Request-Signature"));
    }

    private static int Number(HttpRequest request, string name, int fallback) => !request.Query.ContainsKey(name) ? fallback :
        int.TryParse(request.Query[name], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException();

    internal static async Task<byte[]> ReadBody(HttpRequest request, int maximum, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) throw new ArgumentException();
        if (request.ContentLength > maximum) throw new BadHttpRequestException("Request is too large.", StatusCodes.Status413PayloadTooLarge);
        var bytes = new byte[maximum + 1];
        try
        {
            var length = 0;
            while (length <= maximum)
            {
                var read = await request.Body.ReadAsync(bytes.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length > maximum) throw new BadHttpRequestException("Request is too large.", StatusCodes.Status413PayloadTooLarge);
            return bytes.AsSpan(0, length).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

// Covers company-directory authentication awaits as well as request-body/SQL work.
public sealed class CustomerRequestBoundaryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/internal/customer-requests") && !context.Request.Path.StartsWithSegments("/api/companies") &&
            !context.Request.Path.StartsWithSegments("/internal/bot-config") && !context.Request.Path.StartsWithSegments("/internal/bot-status") &&
            !context.Request.Path.StartsWithSegments("/internal/bot-commands"))
        { await next(context); return; }
        var callerAbort = context.RequestAborted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerAbort);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        context.RequestAborted = deadline.Token;
        try { await next(context); }
        catch (OperationCanceledException) when (callerAbort.IsCancellationRequested) { throw; }
        catch (Exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = "Customer requests are temporarily unavailable." }, callerAbort);
        }
        finally { context.RequestAborted = callerAbort; }
    }
}
