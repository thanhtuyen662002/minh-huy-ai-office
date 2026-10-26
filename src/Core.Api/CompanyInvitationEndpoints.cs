using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

internal static class CompanyInvitationEndpoints
{
    public static void MapCompanyInvitations(this WebApplication app)
    {
        app.MapPost("/api/company/invitations", (HttpRequest http, IRequestAuthorizationContextAccessor accessor,
            CompanyInvitationService service, CancellationToken token) => ExecuteAsync(accessor, async authority =>
        {
            var input = await ReadInputAsync(http, token);
            var result = await service.CreateAsync(authority, input, token);
            return Results.Created($"/api/company/invitations/{result.Id:D}", result);
        })).RequireAuthorization();
        app.MapGet("/api/company/invitations", (HttpRequest http, IRequestAuthorizationContextAccessor accessor,
            CompanyInvitationService service, CancellationToken token) => ExecuteAsync(accessor, async authority =>
        {
            if (http.Query.Any(pair => pair.Key is not ("offset" or "limit") || pair.Value.Count != 1))
                throw new ArgumentException("Invalid page.");
            int Page(string name, int fallback) => http.Query.TryGetValue(name, out var value)
                ? int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? parsed : throw new ArgumentException("Invalid page.") : fallback;
            return Results.Ok(await service.ListAsync(authority, Page("offset", 0), Page("limit", 25), token));
        })).RequireAuthorization();
        app.MapPost("/api/company/invitations/{id:guid}/revoke", (Guid id, HttpRequest http, IRequestAuthorizationContextAccessor accessor,
            CompanyInvitationService service, CancellationToken token) => ExecuteAsync(accessor, async authority =>
        {
            // Revoke has no browser-owned authority or mutable fields.
            if (http.Query.Count != 0 || http.ContentLength > 0 || http.Headers.ContainsKey("Transfer-Encoding")) throw new ArgumentException("Invalid revoke.");
            return Results.Ok(await service.RevokeAsync(authority, id, token));
        })).RequireAuthorization();
    }

    private static async Task<IResult> ExecuteAsync(IRequestAuthorizationContextAccessor accessor,
        Func<Shared.Contracts.AuthorizationContext, Task<IResult>> operation)
    {
        if (accessor.Current?.Context is not { } authority) return Results.Forbid();
        try { return await operation(authority); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (CompanyMembershipNotFoundException) { return Results.NotFound(new { error = "Invitation is unavailable." }); }
        catch (CompanyInvitationConflictException error) { return Results.Conflict(new { error = "Invitation conflicts with current state.", code = error.Code }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "Invitation changed. Reload the invitations." }); }
        catch (BadHttpRequestException error) { return Results.Json(new { error = "Invalid invitation request." }, statusCode: error.StatusCode); }
        catch (Exception error) when (error is ArgumentException or JsonException) { return Results.BadRequest(new { error = "Invalid invitation request." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException)
        { return Results.Json(new { error = "Invitations are temporarily unavailable." }, statusCode: 503); }
    }

    private static async Task<CompanyInvitationInput> ReadInputAsync(HttpRequest request, CancellationToken token)
    {
        if (request.Query.Count != 0 || !request.HasJsonContentType()) throw new ArgumentException("Invalid invitation request.");
        const int maximum = 2048;
        if (request.ContentLength > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        var bytes = new byte[maximum + 1]; var length = 0;
        while (length <= maximum)
        {
            var read = await request.Body.ReadAsync(bytes.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        if (length > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
            root.EnumerateObject().Any(property => property.Name is not ("email" or "roles")) ||
            !root.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array ||
            roles.EnumerateArray().Any(role => role.ValueKind != JsonValueKind.String)) throw new ArgumentException("Invalid invitation request.");
        return new(email.GetString()!, roles.EnumerateArray().Select(role => role.GetString()!).ToArray());
    }
}
