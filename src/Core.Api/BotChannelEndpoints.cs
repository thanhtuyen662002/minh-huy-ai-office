using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api;

internal static class BotChannelEndpoints
{
    private const string Portal = "/api/companies/{companyId:guid}/bot-channels";
    private const string MessagesPortal = "/api/companies/{companyId:guid}/bot-messages";

    internal static bool AddBotChannels(this WebApplicationBuilder builder, bool hasDatabase)
    {
        if (builder.Configuration["AIOffice:BotChannels:Enabled"] != "true" || !hasDatabase) return false;
        var reference = builder.Configuration["AIOffice:BotChannels:EncryptionKeySecretRef"] ?? "";
        if (!SecretReference.TryParse(reference, out var parsed) || parsed!.Provider != "env")
            throw new InvalidOperationException("Invalid bot secret protection configuration.");
        builder.Services.AddScoped(services => new BotSecretProtector(services.GetRequiredService<CompositeSecretResolver>(), reference));
        builder.Services.AddScoped<BotChannelService>(); builder.Services.AddScoped<BotChannelIngress>();
        var hosts = builder.Configuration.GetSection("AIOffice:BotChannels:AllowedAiHosts").GetChildren().Select(x => x.Value ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hosts.Any(x => Uri.CheckHostName(x) == UriHostNameType.Unknown)) throw new InvalidOperationException("Invalid bot AI host configuration.");
        builder.Services.AddHttpClient("BotAiProbe", client => client.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) });
        builder.Services.AddScoped(services => new BotAiConnectionProbe(services.GetRequiredService<IHttpClientFactory>().CreateClient("BotAiProbe"), hosts));
        return true;
    }

    internal static void MapBotChannels(this WebApplication app, bool enabled, bool portalAvailable)
    {
        if (!enabled)
        {
            app.MapGet("/internal/bot-config", () => Results.NotFound());
            app.MapPost("/internal/bot-status", () => Results.NotFound());
            app.MapPost("/internal/bot-commands/{id:guid}/ack", () => Results.NotFound());
            app.MapPost("/internal/bot-messages", () => Results.NotFound());
            MapPortalUnavailable(app, () => Results.NotFound()); return;
        }
        app.MapGet("/internal/bot-config", (HttpRequest request, [FromServices] BotChannelIngress ingress, CancellationToken ct) => Execute(async () =>
        {
            if (request.Query.Count != 1 || request.Query["channel"].Count != 1 || request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0) throw new ArgumentException();
            var channel = BotChannelJson.Channel(request.Query["channel"].ToString());
            // Refuse aliases/escaping/extra query fields so the exact signed target is unambiguous.
            if (request.Path + request.QueryString != "/internal/bot-config?channel=" + channel) throw new ArgumentException();
            return Results.Ok(await ingress.PullAsync(CustomerRequestEndpoints.Signature(request), channel, ct));
        }));
        app.MapPost("/internal/bot-status", (HttpRequest request, [FromServices] BotChannelIngress ingress, CancellationToken ct) => Execute(async () =>
        {
            if (request.Query.Count != 0 || request.Path != "/internal/bot-status") throw new ArgumentException();
            var signature = CustomerRequestEndpoints.Signature(request); var bytes = await CustomerRequestEndpoints.ReadBody(request, BotChannelJson.MaximumBodyBytes, ct);
            try { await ingress.HeartbeatAsync(signature, bytes, ct); return Results.Ok(new { accepted = true }); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }));
        app.MapPost("/internal/bot-commands/{id:guid}/ack", (Guid id, HttpRequest request, [FromServices] BotChannelIngress ingress, CancellationToken ct) => Execute(async () =>
        {
            if (request.Query.Count != 0 || request.Path != $"/internal/bot-commands/{id:D}/ack") throw new ArgumentException();
            var signature = CustomerRequestEndpoints.Signature(request); var bytes = await CustomerRequestEndpoints.ReadBody(request, 1024, ct);
            try { await ingress.AckAsync(signature, id, bytes, ct); return Results.Ok(new { acknowledged = true }); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }));
        app.MapPost("/internal/bot-messages", (HttpRequest request, [FromServices] BotChannelIngress ingress, CancellationToken ct) => Execute(async () =>
        {
            if (request.Query.Count != 0 || request.Path != "/internal/bot-messages") throw new ArgumentException();
            var signature = CustomerRequestEndpoints.Signature(request); var bytes = await CustomerRequestEndpoints.ReadBody(request, BotChannelJson.MaximumBodyBytes, ct);
            try { return Results.Ok(await ingress.MessagesAsync(signature, bytes, ct)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }));
        if (!portalAvailable) { MapPortalUnavailable(app, Unavailable); return; }
        app.MapGet(MessagesPortal, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] BotChannelService service, CancellationToken ct) =>
            Execute(async () => { var authority = Authority(companyId, accessor); NoQuery(request); return Results.Ok(await service.MessageGroupsAsync(authority, ct)); })).RequireAuthorization();
        app.MapGet(MessagesPortal + "/{channel}", (Guid companyId, string channel, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] BotChannelService service, CancellationToken ct) => Execute(async () =>
            {
                var authority = Authority(companyId, accessor);
                if (request.Query.Any(x => x.Key is not ("group" or "before" or "limit") || x.Value.Count != 1) || !request.Query.ContainsKey("group")) throw new ArgumentException();
                DateTimeOffset? before = null;
                if (request.Query.ContainsKey("before"))
                    before = DateTimeOffset.TryParse(request.Query["before"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed.ToUniversalTime() : throw new ArgumentException();
                var limit = request.Query.ContainsKey("limit") ? checked((int)BotChannelJson.Version(request.Query["limit"].ToString())) : 50;
                return Results.Ok(await service.MessagesAsync(authority, channel, request.Query["group"].ToString(), before, limit, ct));
            })).RequireAuthorization();
        app.MapGet(Portal, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] BotChannelService service, CancellationToken ct) =>
            Execute(async () => { var authority = Authority(companyId, accessor); NoQuery(request); return Results.Ok(await service.GetAsync(authority, ct)); })).RequireAuthorization();
        app.MapPut(Portal + "/{channel}", (Guid companyId, string channel, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] BotChannelService service, CancellationToken ct) =>
            Execute(async () =>
            {
                var authority = Authority(companyId, accessor); NoQuery(request); await service.RequireAdminAsync(authority, ct);
                return Results.Ok(await Read<BotChannelUpdate>(request, 524288, input => service.UpdateAsync(authority, channel, input, ct), ct));
            })).RequireAuthorization();
        app.MapPut(Portal + "/ai", (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] BotChannelService service, CancellationToken ct) =>
            Execute(async () =>
            {
                var authority = Authority(companyId, accessor); NoQuery(request); await service.RequireAdminAsync(authority, ct);
                return Results.Ok(await Read<BotAiUpdate>(request, 32768, input => service.UpdateAiAsync(authority, input, ct), ct));
            })).RequireAuthorization();
        app.MapPost(Portal + "/ai/test", (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] BotChannelService service,
            [FromServices] BotAiConnectionProbe probe, CancellationToken ct) => Execute(async () =>
            {
                var authority = Authority(companyId, accessor); NoQuery(request); EmptyBody(request);
                var result = await probe.TestAsync(await service.ProbeSettingsAsync(authority, ct), ct);
                await service.RequireAdminAsync(authority, ct); return Results.Ok(result);
            })).RequireAuthorization();
        app.MapPost(Portal + "/{channel}/commands", (Guid companyId, string channel, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] BotChannelService service, CancellationToken ct) =>
            Execute(async () =>
            {
                var authority = Authority(companyId, accessor); NoQuery(request); await service.RequireAdminAsync(authority, ct);
                return Results.Ok(await Read<BotCommandInput>(request, 1024, input => service.EnqueueAsync(authority, channel, input.Kind, ct), ct));
            })).RequireAuthorization();
        app.MapGet(Portal + "/{channel}/qr", (Guid companyId, string channel, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] BotChannelService service, CancellationToken ct) =>
            Execute(async () => { var authority = Authority(companyId, accessor); NoQuery(request); var qr = await service.QrAsync(authority, channel, ct); return qr is null ? Results.NotFound() : Results.Ok(qr); })).RequireAuthorization();
        app.MapGet(Portal + "/audit", (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] BotChannelService service, CancellationToken ct) =>
            Execute(async () =>
            {
                var authority = Authority(companyId, accessor);
                if (request.Query.Any(x => x.Key is not ("offset" or "limit") || x.Value.Count != 1)) throw new ArgumentException();
                var offset = request.Query.ContainsKey("offset") ? checked((int)BotChannelJson.Version(request.Query["offset"].ToString())) : 0;
                var limit = request.Query.ContainsKey("limit") ? checked((int)BotChannelJson.Version(request.Query["limit"].ToString())) : 50;
                return Results.Ok(await service.AuditAsync(authority, offset, limit, ct));
            })).RequireAuthorization();
    }

    private static void MapPortalUnavailable(WebApplication app, Func<IResult> response)
    {
        app.MapGet(Portal, response); app.MapPut(Portal + "/{channel}", response); app.MapPut(Portal + "/ai", response);
        app.MapPost(Portal + "/ai/test", response); app.MapPost(Portal + "/{channel}/commands", response);
        app.MapGet(Portal + "/{channel}/qr", response); app.MapGet(Portal + "/audit", response);
        app.MapGet(MessagesPortal, response); app.MapGet(MessagesPortal + "/{channel}", response);
    }
    private static AuthorizationContext Authority(Guid companyId, IRequestAuthorizationContextAccessor accessor) =>
        accessor.Current?.Context is { } authority && authority.CompanyId == companyId ? authority : throw new UnauthorizedAccessException();
    private static void NoQuery(HttpRequest request) { if (request.Query.Count != 0) throw new ArgumentException(); }
    private static void EmptyBody(HttpRequest request) { if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0) throw new ArgumentException(); }
    private static async Task<object> Read<T>(HttpRequest request, int limit, Func<T, Task<BotChannelView>> action, CancellationToken ct) =>
        await Read<T, BotChannelView>(request, limit, action, ct);
    private static async Task<object> Read<T>(HttpRequest request, int limit, Func<T, Task<BotAiView>> action, CancellationToken ct) =>
        await Read<T, BotAiView>(request, limit, action, ct);
    private static async Task<object> Read<T>(HttpRequest request, int limit, Func<T, Task<BotCommandView>> action, CancellationToken ct) =>
        await Read<T, BotCommandView>(request, limit, action, ct);
    private static async Task<object> Read<T, TResult>(HttpRequest request, int limit, Func<T, Task<TResult>> action, CancellationToken ct)
    {
        var bytes = await CustomerRequestEndpoints.ReadBody(request, limit, ct);
        try { return (await action(BotChannelJson.Read<T>(bytes)))!; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = "Bot channel access is unavailable." }, statusCode: 403); }
        catch (BotChannelNotFoundException) { return Results.NotFound(new { error = "Bot channel is unavailable." }); }
        catch (Exception error) when (error is BotChannelConflictException or DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Bot settings changed. Reload before updating." }); }
        catch (BadHttpRequestException error) { return Results.Json(new { error = "Invalid bot channel request." }, statusCode: error.StatusCode); }
        catch (Exception error) when (error is ArgumentException or JsonException or OverflowException) { return Results.BadRequest(new { error = "Invalid bot channel request." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or InvalidOperationException or OperationCanceledException) { return Unavailable(); }
    }
    private static IResult Unavailable() => Results.Json(new { error = "Bot channels are temporarily unavailable." }, statusCode: 503);
}
