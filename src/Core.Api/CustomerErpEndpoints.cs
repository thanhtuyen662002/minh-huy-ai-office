using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

public static class CustomerErpEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    public static void MapCustomerErp(this WebApplication app, bool configured, bool authenticated)
    {
        var routes = app.MapGroup("/api/data-sources");
        if (!configured || !authenticated)
        {
            static IResult Unavailable() => Results.Json(new { code = "unavailable" }, statusCode: 503);
            routes.MapPost("/connection-test", Unavailable);
            routes.MapPost("/reader-login", Unavailable);
            routes.MapGet("/customer-erp", Unavailable);
            routes.MapPost("/customer-erp", Unavailable);
            routes.MapPut("/{id:guid}/credentials", Unavailable);
            return;
        }
        routes.RequireAuthorization();
        routes.MapGet("/customer-erp", (HttpRequest http, IRequestAuthorizationContextAccessor accessor, CustomerErpService service) =>
            Handle(http, accessor, (authority, token) => service.ListAsync(authority, token)));
        routes.MapPost("/connection-test", (IRequestAuthorizationContextAccessor accessor, CustomerErpService service,
            HttpRequest http) => Handle(http, accessor, async (authority, token) =>
                await service.TestAsync(authority, await ReadAsync<CustomerErpConnectionInput>(http, token), token)))
            .RequireRateLimiting("customer-erp");
        // Uses administrator credentials once to create a read-only login; they are never stored.
        routes.MapPost("/reader-login", async (IRequestAuthorizationContextAccessor accessor, CustomerErpService service,
            ICustomerErpReaderProvisioner provisioner, HttpRequest http) => await Handle(http, accessor, async (authority, token) =>
            {
                var result = await service.CreateReaderAsync(authority, await ReadAsync<CustomerErpConnectionInput>(http, token), provisioner, token);
                return result.Code == "created" ? result : throw new CustomerErpVerificationException(new(result.Code));
            }))
            .RequireRateLimiting("customer-erp");
        routes.MapPost("/customer-erp", (IRequestAuthorizationContextAccessor accessor, CustomerErpService service,
            HttpRequest http) => Handle(http, accessor, async (authority, token) =>
                await service.SaveAsync(authority, await ReadAsync<CustomerErpSaveRequest>(http, token), token: token)))
            .RequireRateLimiting("customer-erp");
        routes.MapPut("/{id:guid}/credentials", (Guid id, IRequestAuthorizationContextAccessor accessor, CustomerErpService service,
            HttpRequest http) => Handle(http, accessor, async (authority, token) =>
                await service.SaveAsync(authority, await ReadAsync<CustomerErpSaveRequest>(http, token), id, token)))
            .RequireRateLimiting("customer-erp");
    }

    private static async Task<IResult> Handle<T>(HttpRequest http, IRequestAuthorizationContextAccessor accessor,
        Func<MinhHuy.AIOffice.Shared.Contracts.AuthorizationContext, CancellationToken, Task<T>> action)
    {
        if (accessor.Current is not { } current || !current.Roles.Contains("admin", StringComparer.Ordinal)) return Results.Forbid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (http.Query.Count != 0) throw new ArgumentException();
            return Results.Ok(await action(current.Context, deadline.Token));
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (Exception error) when (error is ArgumentException or JsonException or DecoderFallbackException)
        { return Results.BadRequest(new { code = "invalid_fields" }); }
        catch (BadHttpRequestException error) { return Results.Json(new { code = "invalid_fields" }, statusCode: error.StatusCode == 413 ? 413 : 400); }
        catch (CustomerErpVerificationException error) { return Results.Json(error.Result, statusCode: 422); }
        catch (DataSourceRegistrationConflictException) { return Results.Conflict(new { code = "conflict" }); }
        catch (DbUpdateException) { return Results.Conflict(new { code = "conflict" }); }
        catch (Exception error) when (error is InvalidOperationException or DbException)
        { return Results.Json(new { code = "unavailable" }, statusCode: 503); }
        catch (OperationCanceledException) when (!http.HttpContext.RequestAborted.IsCancellationRequested)
        { return Results.Json(new { code = "unavailable" }, statusCode: 503); }
    }

    private static async Task<T> ReadAsync<T>(HttpRequest http, CancellationToken token) where T : class
    {
        const int maximum = 16384;
        if (!http.HasJsonContentType()) throw new ArgumentException();
        if (http.ContentLength > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        var bytes = new byte[maximum + 1]; var length = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (length <= maximum)
            {
                var read = await http.Body.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > maximum) throw new BadHttpRequestException("Request is too large.", 413);
            _ = new UTF8Encoding(false, true).GetCharCount(bytes.AsSpan(0, length));
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
                .GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)) throw new ArgumentException();
            return document.RootElement.Deserialize<T>(Json) ?? throw new ArgumentException();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
