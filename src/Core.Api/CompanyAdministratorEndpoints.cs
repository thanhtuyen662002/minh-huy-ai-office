using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

internal static class CompanyAdministratorEndpoints
{
    public static void MapCompanyAdministrator(this WebApplication app) =>
        app.MapPost("/api/company/members/{userId:guid}/administrator", async (Guid userId, HttpRequest http,
            IRequestAuthorizationContextAccessor accessor, CompanyAdministratorService service, CancellationToken cancellationToken) =>
        {
            var authority = accessor.Current?.Context;
            if (authority is null) return (IResult)Results.Forbid();
            try
            {
                var request = await ReadRequestAsync(http, cancellationToken);
                return Results.Ok(await service.SetAdministratorAsync(authority, userId, request, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (CompanyMembershipNotFoundException) { return Results.NotFound(new { error = "Membership is unavailable." }); }
            catch (CompanyMembershipAccessConflictException error)
            {
                return Results.Conflict(new { error = "Administrator role conflicts with current state.", code = error.Code });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Administrator role changed. Reload the directory.", code = "stale-version" }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Administrator role could not be saved. Retry the same operation." }); }
            catch (BadHttpRequestException error) { return Results.Json(new { error = "Invalid administrator role request." }, statusCode: error.StatusCode); }
            catch (Exception error) when (error is ArgumentException or JsonException)
            {
                return Results.BadRequest(new { error = "Invalid administrator role request." });
            }
            catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException)
            {
                return Results.Json(new { error = "Administrator role is temporarily unavailable. Retry the same operation." }, statusCode: 503);
            }
        }).RequireAuthorization();

    private static async Task<CompanyAdministratorRequest> ReadRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        const int maximum = 2048;
        if (!request.HasJsonContentType()) throw new ArgumentException("JSON is required.");
        if (request.ContentLength > maximum) throw new BadHttpRequestException("Request is too large.", StatusCodes.Status413PayloadTooLarge);
        var bytes = new byte[maximum + 1];
        var length = 0;
        while (length <= maximum)
        {
            var read = await request.Body.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > maximum) throw new BadHttpRequestException("Request is too large.", StatusCodes.Status413PayloadTooLarge);
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new ArgumentException("Invalid request properties.");
        return document.RootElement.Deserialize<CompanyAdministratorRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = false }) ?? throw new ArgumentException("Missing request.");
    }
}
