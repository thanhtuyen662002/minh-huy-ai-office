using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

// Trợ lý pictures (attached to questions) and deleting questions from the asker's own history.
internal static class AssistantEndpoints
{
    private const string ImagesPath = "/api/companies/{companyId:guid}/assistant-images";
    private const string HidePath = "/api/companies/{companyId:guid}/assistant-history/hide";
    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"];

    internal static void MapAssistant(this WebApplication app, bool available)
    {
        if (!available)
        {
            app.MapPost(ImagesPath, () => Results.NotFound());
            app.MapGet(ImagesPath + "/{id:guid}", () => Results.NotFound());
            app.MapPost(HidePath, () => Results.NotFound());
            return;
        }
        app.MapPost(ImagesPath, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] AssistantService assistant, CancellationToken ct) => Execute(accessor, companyId, request, async authority =>
            {
                var type = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
                if (type is null || !ImageTypes.Contains(type)) throw new ArgumentException("Picture type is invalid.");
                var bytes = await ReadBody(request, AssistantService.MaximumImageBytes, ct);
                var receipt = await assistant.UploadAsync(authority, type, bytes, ct);
                return Results.Ok(new { id = receipt.Id, contentType = receipt.ContentType, bytes = receipt.Bytes });
            })).RequireAuthorization();
        app.MapGet(ImagesPath + "/{id:guid}", (Guid companyId, Guid id, HttpRequest request, HttpResponse response,
            IRequestAuthorizationContextAccessor accessor, [FromServices] AssistantService assistant, CancellationToken ct) =>
            Execute(accessor, companyId, request, async authority =>
            {
                var image = await assistant.ImageAsync(authority, id, ct);
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                return Results.File(image.Content, image.ContentType);
            })).RequireAuthorization();
        app.MapPost(HidePath, (Guid companyId, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] AssistantService assistant, CancellationToken ct) => Execute(accessor, companyId, request, async authority =>
            {
                var bytes = await ReadBody(request, 16 * 1024, ct);
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                    || !root.TryGetProperty("taskIds", out var list) || list.ValueKind != JsonValueKind.Array) throw new ArgumentException();
                var ids = list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && Guid.TryParseExact(item.GetString(), "D", out var id)
                    ? id : throw new ArgumentException()).ToArray();
                return Results.Ok(new { hidden = await assistant.HideTasksAsync(authority, ids, ct) });
            })).RequireAuthorization();
    }

    private static async Task<byte[]> ReadBody(HttpRequest request, int maximum, CancellationToken ct)
    {
        if (request.ContentLength > maximum) throw new ArgumentException("Body is too large.");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximum) throw new ArgumentException("Body is too large.");
        }
        return buffer.ToArray();
    }

    private static async Task<IResult> Execute(IRequestAuthorizationContextAccessor accessor, Guid companyId, HttpRequest request,
        Func<Shared.Contracts.AuthorizationContext, Task<IResult>> action)
    {
        try
        {
            var authority = accessor.Current?.Context;
            if (authority is null || authority.CompanyId != companyId) return Results.Forbid();
            if (request.Query.Count != 0) throw new ArgumentException();
            return await action(authority);
        }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = "Trợ lý is unavailable." }, statusCode: 403); }
        catch (AssistantImageNotFoundException) { return Results.NotFound(new { error = "Picture is unavailable." }); }
        catch (BadHttpRequestException error) { return Results.Json(new { error = "Invalid request." }, statusCode: error.StatusCode); }
        catch (Exception error) when (error is ArgumentException or JsonException) { return Results.BadRequest(new { error = "Invalid request." }); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Daily picture limit", StringComparison.Ordinal))
        { return Results.Json(new { error = "Daily picture limit reached." }, statusCode: 429); }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or InvalidOperationException or OperationCanceledException)
        { return Results.Json(new { error = "Trợ lý is temporarily unavailable." }, statusCode: 503); }
    }
}
