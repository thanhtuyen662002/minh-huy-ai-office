using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CoreApiGroupEnrollmentTests
{
    private const string Path = "/internal/group-ingress/enrollment";

    [Fact]
    public async Task DefaultEndpointIsUnavailableWithNoStore()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        { builder.UseSetting("AIOffice:GroupIntake:Enabled", "false"); builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", ""); });
        using var client = factory.CreateClient(); using var response = await client.PostAsJsonAsync(Path, new { source = "untrusted" });
        await BoundedAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RealEndpointReadsCurrentMetadataWithoutLeaseContentKeyOrPortalEffects()
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(sourceKeyAvailable: false, seedLease: false);
        using var client = fixture.Factory.CreateClient(); using var request = Request(fixture);
        using var response = await client.SendAsync(request); await BoundedAsync(response, HttpStatusCode.OK);
        var snapshot = (await response.Content.ReadFromJsonAsync<GroupConnectorEnrollmentSnapshot>())!;
        Assert.Equal(fixture.Auth.Scope, snapshot.Source.Scope); Assert.Equal(fixture.Auth.External, snapshot.Source.ExternalIdentity);
        Assert.Equal(fixture.Auth.Clock.Current, snapshot.CheckedAtUtc); Assert.Equal(fixture.Auth.Account.Version, snapshot.AccountVersion);
        Assert.Empty(fixture.Auth.Db.GroupListenerLeases); Assert.Empty(fixture.Auth.Db.GroupListenerCommandReceipts);
        Assert.Empty(fixture.Auth.Db.GroupAccountCoverageGaps); await fixture.EmptyAsync();
        Assert.Empty(fixture.Auth.Db.Tasks); Assert.Empty(fixture.Auth.Db.CompanyMemberships);
        var keys = fixture.Auth.Secrets.Calls;
        fixture.Auth.Grant.IsEnabled = false; fixture.Auth.Db.SaveChanges();
        using var fresh = Request(fixture); using var denied = await client.SendAsync(fresh);
        await BoundedAsync(denied, HttpStatusCode.Forbidden); Assert.Equal(keys, fixture.Auth.Secrets.Calls);
        fixture.Auth.Grant.IsEnabled = true; fixture.Auth.Db.SaveChanges();
        using var restored = Request(fixture); using var allowed = await client.SendAsync(restored);
        await BoundedAsync(allowed, HttpStatusCode.OK); await fixture.EmptyAsync();
    }

    [Theory]
    [InlineData("event-domain", HttpStatusCode.Forbidden)]
    [InlineData("listener-domain", HttpStatusCode.Forbidden)]
    [InlineData("foreign-source", HttpStatusCode.Forbidden)]
    [InlineData("unknown-field", HttpStatusCode.Forbidden)]
    [InlineData("duplicate-field", HttpStatusCode.Forbidden)]
    [InlineData("invalid-utf8", HttpStatusCode.Forbidden)]
    [InlineData("missing-service", HttpStatusCode.BadRequest)]
    [InlineData("duplicate-epoch", HttpStatusCode.BadRequest)]
    [InlineData("query", HttpStatusCode.BadRequest)]
    [InlineData("text-content", HttpStatusCode.UnsupportedMediaType)]
    public async Task InvalidMetadataTransportCannotReadAuthorityOrWriteGraph(string change, HttpStatusCode status)
    {
        await using var fixture = new CoreApiGroupIngressTests.Fixture(seedLease: false); using var client = fixture.Factory.CreateClient();
        var payload = new GroupConnectorEnrollmentRequest(fixture.Auth.Scope, fixture.Auth.External);
        if (change == "foreign-source") payload = payload with { Source = payload.Source with { SourceBindingId = Guid.NewGuid() } };
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, GroupServiceAuthenticator.JsonOptions);
        if (change == "unknown-field") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Insert(1, "\"secretRef\":\"PRIVATE_FIXTURE\","));
        if (change == "duplicate-field") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Insert(1, "\"source\":null,"));
        if (change == "invalid-utf8") body = [0xff];
        using var request = Request(fixture, body, change);
        if (change == "missing-service") request.Headers.Remove("X-AIOffice-Group-Service");
        if (change == "duplicate-epoch") request.Headers.Add("X-AIOffice-Group-Epoch", "1");
        if (change == "query") request.RequestUri = new Uri(Path + "?secretRef=untrusted", UriKind.Relative);
        if (change == "text-content") request.Content!.Headers.ContentType = new("text/plain");
        using var response = await client.SendAsync(request); await BoundedAsync(response, status);
        await fixture.EmptyAsync(); Assert.Empty(fixture.Auth.Db.GroupListenerLeases);
        Assert.Empty(fixture.Auth.Db.GroupListenerCommandReceipts); Assert.Empty(fixture.Auth.Db.GroupAccountCoverageGaps);
    }

    private static HttpRequestMessage Request(CoreApiGroupIngressTests.Fixture fixture, byte[]? body = null, string? domain = null)
    {
        body ??= JsonSerializer.SerializeToUtf8Bytes(new GroupConnectorEnrollmentRequest(fixture.Auth.Scope, fixture.Auth.External), GroupServiceAuthenticator.JsonOptions);
        var request = fixture.Request(body: body); request.RequestUri = new Uri(Path, UriKind.Relative);
        var signature = fixture.Auth.Sign(body: body);
        var signing = domain switch
        {
            "event-domain" => GroupServiceAuthenticator.SigningBytes(signature, body),
            "listener-domain" => GroupServiceAuthenticator.ListenerSigningBytes(signature, body),
            _ => GroupServiceAuthenticator.EnrollmentSigningBytes(signature, body)
        };
        request.Headers.Remove("X-AIOffice-Group-Signature");
        request.Headers.Add("X-AIOffice-Group-Signature", Convert.ToHexString(HMACSHA256.HashData(fixture.Auth.Secrets.Key, signing)));
        return request;
    }
    private static async Task BoundedAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsByteArrayAsync(); Assert.InRange(body.Length, 0, 8192);
        var text = Encoding.UTF8.GetString(body); Assert.DoesNotContain("secretref", text); Assert.DoesNotContain("PRIVATE_FIXTURE", text);
        Assert.DoesNotContain("Exception", text); Assert.DoesNotContain("original", text);
    }
}
