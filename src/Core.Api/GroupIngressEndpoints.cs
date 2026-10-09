using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Core.Api;

internal static class GroupIngressEndpoints
{
    internal const string Path = "/internal/group-ingress/events";

    internal static bool AddGroupIngress(this WebApplicationBuilder builder, bool hasPlatformDatabase, CompositeSecretResolver secrets)
    {
        if (builder.Configuration["AIOffice:GroupIntake:Enabled"] != "true" || !hasPlatformDatabase) return false;
        var synthetic = builder.Configuration["AIOffice:GroupIntake:OwnedSyntheticFixture"] == "true";
        var policy = synthetic ? GroupIngressRuntimePolicy.OwnedSyntheticFixture(builder.Environment.EnvironmentName,
            builder.Configuration["AIOffice:GroupIntake:OwnedDisposableFixture"] == "true") : GroupIngressRuntimePolicy.Live;
        var keys = ReadKeys(builder.Configuration.GetSection("AIOffice:GroupIntake:SourceKeys"));
        // Validate the complete immutable enrollment before serving requests.
        // A lazy DI factory would fail outside the bounded HTTP error handler.
        var sourceKeys = new ConfiguredGroupSourceKeyProvider(secrets, keys);
        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<GroupSourceContentProtector>();
        builder.Services.AddSingleton<IGroupSourceKeyProvider>(sourceKeys);
        builder.Services.AddScoped<GroupServiceAuthenticator>();
        builder.Services.AddScoped<GroupIngressStore>();
        builder.Services.AddScoped<GroupSourceReader>();
        return true;
    }

    internal static void MapGroupIngress(this WebApplication app, bool enabled)
    {
        if (!enabled) { app.MapPost(Path, () => Results.NotFound()); return; }
        app.MapPost(Path, async (HttpRequest request, [FromServices] GroupServiceAuthenticator authentication,
            [FromServices] GroupIngressStore store, CancellationToken cancellationToken) =>
        {
            byte[]? body = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (request.Query.Count != 0) throw new ArgumentException();
                var signature = Signature(request);
                body = await ReadBodyAsync(request, deadline.Token);
                var verified = await authentication.AuthenticateAsync(signature, body, deadline.Token);
                return (IResult)Results.Ok(await store.AcceptAsync(verified, deadline.Token));
            }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "Group service access is not available." }, statusCode: 403); }
            catch (GroupIngressConflictException) { return Results.Json(new { error = "Group event identity conflicts with a committed receipt." }, statusCode: 409); }
            catch (BadHttpRequestException error) { return Results.Json(new { error = "Invalid group ingress request." }, statusCode: error.StatusCode); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid group ingress request." }); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Results.Json(new { error = "Group ingress is unavailable. Reconcile the same event." }, statusCode: 503); }
            catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException)
            { return Results.Json(new { error = "Group ingress is unavailable." }, statusCode: 503); }
            finally { if (body is not null) CryptographicOperations.ZeroMemory(body); }
        });
    }

    private static GroupServiceSignature Signature(HttpRequest request)
    {
        static string Header(HttpRequest request, string name, int maximum)
        {
            if (!request.Headers.TryGetValue(name, out var values) || values.Count != 1 || values[0] is not { } value || value.Length > maximum) throw new ArgumentException();
            return value;
        }
        static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value ? id : throw new ArgumentException();
        static long Number(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number.ToString(CultureInfo.InvariantCulture) == value ? number : throw new ArgumentException();
        return new(Id(Header(request, "X-AIOffice-Group-Service", 36)), Number(Header(request, "X-AIOffice-Group-Epoch", 19)),
            Number(Header(request, "X-AIOffice-Group-Signed-At", 19)), Id(Header(request, "X-AIOffice-Group-Nonce", 36)),
            Header(request, "X-AIOffice-Group-Signature", 64));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        const int maximum = GroupServiceAuthenticator.MaximumBodyBytes;
        if (!request.HasJsonContentType()) throw new BadHttpRequestException("JSON is required.", 415);
        if (request.ContentLength > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        var scratch = new byte[maximum + 1]; var length = 0;
        try
        {
            while (length <= maximum)
            {
                var count = await request.Body.ReadAsync(scratch.AsMemory(length), cancellationToken);
                if (count == 0) break;
                length += count;
            }
            if (length > maximum) throw new BadHttpRequestException("Request is too large.", 413);
            return scratch.AsSpan(0, length).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(scratch); }
    }

    private static IReadOnlyList<GroupSourceKeyBinding> ReadKeys(IConfigurationSection configuration)
    {
        try
        {
            var rows = configuration.GetChildren().Take(4097).ToArray();
            if (rows.Length > 4096) throw new ArgumentException();
            return rows.Select(row => new GroupSourceKeyBinding(new GroupScope(Guid.Parse(row["TenantId"]!), Guid.Parse(row["CompanyId"]!),
                Guid.Parse(row["SourceBindingId"]!)), row["KeyId"]!, SecretReference.Parse(row["SecretRef"]!), bool.Parse(row["IsWriteKey"]!))).ToArray();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
        { throw new InvalidOperationException("Group source key configuration is invalid."); }
    }
}
