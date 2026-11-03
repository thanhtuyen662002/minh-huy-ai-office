using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

// Kiến thức pages: any active company member may read and edit them.
internal static class KnowledgeEndpoints
{
    private const string PagesPath = "/api/companies/{companyId:guid}/knowledge-pages";

    internal static void MapKnowledge(this WebApplication app, bool available)
    {
        if (!available)
        {
            app.MapGet(PagesPath, () => Results.NotFound());
            app.MapPost(PagesPath, () => Results.NotFound());
            app.MapGet(PagesPath + "/{id:guid}", () => Results.NotFound());
            app.MapPut(PagesPath + "/{id:guid}", () => Results.NotFound());
            app.MapDelete(PagesPath + "/{id:guid}", () => Results.NotFound());
            app.MapPost(PagesPath + "/extract", () => Results.NotFound());
            return;
        }
        app.MapGet(PagesPath, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] KnowledgeBase pages,
            CancellationToken ct) => Execute(accessor, companyId, request, async authority => Results.Ok(await pages.ListAsync(authority, companyId, ct)))).RequireAuthorization();
        app.MapPost(PagesPath, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor, [FromServices] KnowledgeBase pages,
            CancellationToken ct) => Execute(accessor, companyId, request, async authority =>
            {
                var bytes = await CustomerRequestEndpoints.ReadBody(request, KnowledgePageJson.MaximumBytes, ct);
                try { return Results.Ok(await pages.CreateAsync(authority, companyId, KnowledgePageJson.Read(bytes, update: false), ct)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            })).RequireAuthorization();
        app.MapPost(PagesPath + "/extract", (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] KnowledgeBase pages, CancellationToken ct) => Execute(accessor, companyId, request, async authority =>
            {
                // The raw file is the body and its name travels in the query.
                var name = request.Query["fileName"];
                if (name.Count != 1 || string.IsNullOrWhiteSpace(name[0]) || name[0]!.Length > KnowledgeFileText.MaximumFileName
                    || !string.Equals(request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
                var bytes = await ReadFile(request, ct);
                var extract = await pages.ExtractAsync(authority, companyId, name[0]!, bytes, ct);
                return Results.Json(new { title = extract.Title, parts = extract.Parts, truncated = extract.Truncated }, RelaxedJson);
            }, allowedQuery: "fileName")).RequireAuthorization();
        app.MapGet(PagesPath + "/{id:guid}", (Guid companyId, Guid id, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] KnowledgeBase pages, CancellationToken ct) =>
            Execute(accessor, companyId, request, async authority => Results.Ok(await pages.GetAsync(authority, companyId, id, ct)))).RequireAuthorization();
        app.MapPut(PagesPath + "/{id:guid}", (Guid companyId, Guid id, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] KnowledgeBase pages, CancellationToken ct) => Execute(accessor, companyId, request, async authority =>
            {
                var bytes = await CustomerRequestEndpoints.ReadBody(request, KnowledgePageJson.MaximumBytes, ct);
                try { return Results.Ok(await pages.UpdateAsync(authority, companyId, id, KnowledgePageJson.Read(bytes, update: true), ct)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            })).RequireAuthorization();
        app.MapDelete(PagesPath + "/{id:guid}", (Guid companyId, Guid id, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] KnowledgeBase pages, CancellationToken ct) => Execute(accessor, companyId, request, async authority =>
            {
                // The version travels in the query so DELETE needs no body.
                var version = request.Query["rowVersion"];
                if (version.Count != 1) throw new ArgumentException();
                await pages.DeleteAsync(authority, companyId, id, version[0]!, ct);
                return Results.NoContent();
            }, allowedQuery: "rowVersion")).RequireAuthorization();
    }

    // Vietnamese text stays UTF-8 instead of \uXXXX escapes so a long file fits the web response bound.
    private static readonly JsonSerializerOptions RelaxedJson = new(JsonSerializerDefaults.Web)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static async Task<byte[]> ReadFile(HttpRequest request, CancellationToken ct)
    {
        var maximum = KnowledgeFileText.MaximumFileBytes;
        if (request.ContentLength > maximum) throw new KnowledgeFileUnreadableException("tooLarge");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximum) throw new KnowledgeFileUnreadableException("tooLarge");
        }
        return buffer.ToArray();
    }

    private static async Task<IResult> Execute(IRequestAuthorizationContextAccessor accessor, Guid companyId, HttpRequest request,
        Func<Shared.Contracts.AuthorizationContext, Task<IResult>> action, string? allowedQuery = null)
    {
        try
        {
            var authority = accessor.Current?.Context;
            if (authority is null || authority.CompanyId != companyId) return Results.Forbid();
            foreach (var query in request.Query)
                if (query.Key != allowedQuery || query.Value.Count != 1) throw new ArgumentException();
            return await action(authority);
        }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = "Knowledge pages are unavailable." }, statusCode: 403); }
        catch (KnowledgeFileUnreadableException error)
        { return Results.Json(new { error = "Knowledge file is unreadable.", reason = error.Reason }, statusCode: 422); }
        catch (KnowledgePageNotFoundException) { return Results.NotFound(new { error = "Knowledge page is unavailable." }); }
        catch (Exception error) when (error is CustomerRequestConflictException or DbUpdateConcurrencyException)
        { return Results.Conflict(new { error = "Knowledge page changed. Reload it before saving." }); }
        catch (BadHttpRequestException error) { return Results.Json(new { error = "Invalid knowledge page." }, statusCode: error.StatusCode); }
        catch (Exception error) when (error is ArgumentException or JsonException) { return Results.BadRequest(new { error = "Invalid knowledge page." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or InvalidOperationException or OperationCanceledException)
        { return Results.Json(new { error = "Knowledge pages are temporarily unavailable." }, statusCode: 503); }
    }
}
