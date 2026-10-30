using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

// Telegram bot for technicians: members link their Telegram account from the web, then the bot claims and
// updates YC tickets for them through signed calls. Off unless AIOffice:Telegram:Enabled and customer requests are on.
internal static class TelegramTechEndpoints
{
    private const string LinkPath = "/api/companies/{companyId:guid}/telegram-link";
    private static readonly string[] BridgePaths = [TelegramTechBridge.LinkPath, TelegramTechBridge.ActionPath, TelegramTechBridge.TicketsPath, TelegramTechBridge.TicketPath];

    internal static bool AddTelegramTech(this WebApplicationBuilder builder, bool customerRequestsEnabled)
    {
        if (!customerRequestsEnabled || builder.Configuration["AIOffice:Telegram:Enabled"] != "true") return false;
        builder.Services.AddSingleton(TelegramOptions.Create(builder.Configuration["AIOffice:Telegram:BotUsername"]));
        builder.Services.AddScoped<TelegramLinkPortal>();
        builder.Services.AddScoped<TelegramTechBridge>();
        return true;
    }

    internal static void MapTelegramTech(this WebApplication app, bool enabled, bool portalAvailable)
    {
        if (!enabled)
        {
            foreach (var path in BridgePaths) app.MapPost(path, () => Results.NotFound());
            app.MapGet(LinkPath, () => Results.NotFound());
            app.MapPost(LinkPath, () => Results.NotFound());
            app.MapDelete(LinkPath, () => Results.NotFound());
            return;
        }
        Bridge(app, TelegramTechBridge.LinkPath, (bridge, signature, bytes, ct) => Ok(bridge.LinkAsync(signature, bytes, ct)));
        Bridge(app, TelegramTechBridge.ActionPath, (bridge, signature, bytes, ct) => Ok(bridge.ActAsync(signature, bytes, ct)));
        Bridge(app, TelegramTechBridge.TicketsPath, (bridge, signature, bytes, ct) => Ok(bridge.ListAsync(signature, bytes, ct)));
        Bridge(app, TelegramTechBridge.TicketPath, (bridge, signature, bytes, ct) => Ok(bridge.GetAsync(signature, bytes, ct)));

        if (!portalAvailable)
        {
            IResult Unavailable() => Results.Json(new { error = "Customer requests are temporarily unavailable." }, statusCode: 503);
            app.MapGet(LinkPath, Unavailable);
            app.MapPost(LinkPath, Unavailable);
            app.MapDelete(LinkPath, Unavailable);
            return;
        }
        app.MapGet(LinkPath, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] TelegramLinkPortal portal,
            CancellationToken ct) => Member(accessor, companyId, request, async authority => Results.Ok(await portal.GetAsync(authority, companyId, ct)))).RequireAuthorization();
        app.MapPost(LinkPath, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] TelegramLinkPortal portal,
            CancellationToken ct) => Member(accessor, companyId, request, async authority => Results.Ok(await portal.CreateCodeAsync(authority, companyId, ct)))).RequireAuthorization();
        app.MapDelete(LinkPath, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] TelegramLinkPortal portal,
            CancellationToken ct) => Member(accessor, companyId, request, async authority =>
            {
                await portal.UnlinkAsync(authority, companyId, ct);
                return Results.NoContent();
            })).RequireAuthorization();
    }

    private static async Task<IResult> Ok<T>(Task<T> operation) => Results.Ok(await operation);

    private static void Bridge(WebApplication app, string path, Func<TelegramTechBridge, CustomerRequestSignature, byte[], CancellationToken, Task<IResult>> operation) =>
        app.MapPost(path, (HttpRequest request, [FromServices] TelegramTechBridge bridge, CancellationToken ct) => CustomerRequestEndpoints.Execute(async () =>
        {
            if (request.Query.Count != 0 || request.Path.Value != path || request.Method != "POST") throw new ArgumentException();
            var signature = CustomerRequestEndpoints.Signature(request);
            var bytes = await CustomerRequestEndpoints.ReadBody(request, TelegramTechJson.MaximumBytes, ct);
            try { return await operation(bridge, signature, bytes, ct); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }));

    private static Task<IResult> Member(IRequestAuthorizationContextAccessor accessor, Guid companyId, HttpRequest request,
        Func<Shared.Contracts.AuthorizationContext, Task<IResult>> action) => CustomerRequestEndpoints.Execute(async () =>
        {
            var authority = accessor.Current?.Context;
            if (authority is null || authority.CompanyId != companyId) return Results.Forbid();
            if (request.Query.Count != 0) throw new ArgumentException();
            return await action(authority);
        });
}
