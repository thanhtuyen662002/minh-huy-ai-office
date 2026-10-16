using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupConnectorEnrollmentTests
{
    [Fact]
    public async Task SignedMetadataFetchBootstrapsQualifiedCurrentSourceWithoutContentLeaseOrPortalIdentity()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture();
        var request = new GroupConnectorEnrollmentRequest(fixture.Scope, fixture.External);
        var calls = 0;
        using var client = Client(fixture, async (http, token) =>
        {
            calls++;
            Assert.Equal("/internal/group-ingress/enrollment", http.RequestUri!.AbsolutePath);
            Assert.False(http.Headers.Contains("Authorization"));
            var body = await http.Content!.ReadAsByteArrayAsync(token);
            var signature = Signature(http);
            var independent = Encoding.ASCII.GetBytes(FormattableString.Invariant(
                $"aioffice-group-enrollment-v1\n{signature.ServiceId:D}\n{signature.CredentialEpoch}\n{signature.SignedAtUnixSeconds}\n{signature.Nonce:D}\n{Convert.ToHexString(SHA256.HashData(body))}"));
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(fixture.Secrets.Key, independent)), signature.SignatureHex);
            var snapshot = await fixture.Authenticator.AuthenticateEnrollmentAsync(signature, body, token);
            var serialized = JsonSerializer.Serialize(snapshot, GroupServiceAuthenticator.JsonOptions);
            Assert.DoesNotContain("secretref", serialized); Assert.DoesNotContain("original", serialized);
            Assert.Equal(fixture.Account.Version, snapshot.AccountVersion);
            return Reply(http, snapshot);
        });
        var current = await client.FetchEnrollmentAsync(request);
        Assert.Equal(fixture.Scope, current.Enrollment.Source.Scope);
        Assert.Equal(fixture.External, current.Enrollment.Source.ExternalIdentity);
        Assert.Equal(fixture.Clock.Current, current.CheckedAtUtc); Assert.Equal(1, calls);
        Assert.Empty(fixture.Db.GroupListenerLeases); Assert.Empty(fixture.Db.GroupListenerCommandReceipts);
        Assert.Empty(fixture.Db.GroupMessages); Assert.Empty(fixture.Db.GroupIngressOutbox);
        Assert.Empty(fixture.Db.Tasks); Assert.Empty(fixture.Db.CompanyMemberships);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("source")]
    [InlineData("role")]
    [InlineData("service")]
    [InlineData("epoch")]
    [InlineData("account")]
    [InlineData("account-alias")]
    [InlineData("hash")]
    [InlineData("grant")]
    [InlineData("capability")]
    public async Task RegistryRefusalPrecedesCredentialResolution(string change)
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(change);
        var body = Body(fixture);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateEnrollmentAsync(Sign(fixture, body), body));
        Assert.Equal(0, fixture.Secrets.Calls);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("source")]
    public async Task ExactDeclaredSourceMustMatchBackendIdentityBeforeKeyResolution(string change)
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture();
        var source = fixture.Scope;
        source = change switch
        {
            "tenant" => source with { TenantId = Guid.NewGuid() },
            "company" => source with { CompanyId = Guid.NewGuid() },
            _ => source with { SourceBindingId = Guid.NewGuid() }
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(new GroupConnectorEnrollmentRequest(source, fixture.External), GroupServiceAuthenticator.JsonOptions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateEnrollmentAsync(Sign(fixture, body), body));
        Assert.Equal(0, fixture.Secrets.Calls);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("grant")]
    [InlineData("epoch")]
    [InlineData("qualification")]
    public async Task FreshFinalAuthorityAndQualificationAreRecheckedAfterCredentialAwait(string change)
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(controlled: true);
        fixture.Secrets.BeforeResolution = () =>
        {
            if (change == "source") fixture.Binding.IsEnabled = false;
            if (change == "grant") fixture.Grant.IsEnabled = false;
            if (change == "epoch") fixture.Service.CredentialEpoch++;
            if (change == "qualification") fixture.Account.QualificationJson = "{\"environment\":2,\"observations\":[]}";
            fixture.Db.SaveChanges();
        };
        var body = Body(fixture);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateEnrollmentAsync(Sign(fixture, body), body));
        Assert.Equal(1, fixture.Secrets.Calls); Assert.Empty(fixture.Db.GroupMessages);
    }

    [Fact]
    public async Task FinalQualificationIsCopiedAndRequestBytesCannotChangeWhileResolvingKey()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(controlled: true);
        var body = Body(fixture);
        fixture.Secrets.BeforeResolution = () => { Array.Fill(body, (byte)0); fixture.AllowControlledReceive(fixture.Clock.Current.AddSeconds(-1)); };
        var result = await fixture.Authenticator.AuthenticateEnrollmentAsync(Sign(fixture, body), body);
        Assert.Equal(fixture.External, result.Source.ExternalIdentity);
        Assert.All(result.Observations, observation => Assert.Equal(fixture.Clock.Current.AddSeconds(-1), observation.ObservedAtUtc));
        Assert.All(body, value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("listener")]
    public async Task AuthenticationDomainsCannotBeSubstituted(string domain)
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var body = Body(fixture);
        var signature = Sign(fixture, body);
        signature = signature with
        {
            SignatureHex = Convert.ToHexString(HMACSHA256.HashData(fixture.Secrets.Key,
            domain == "event" ? GroupServiceAuthenticator.SigningBytes(signature, body) : GroupServiceAuthenticator.ListenerSigningBytes(signature, body)))
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateEnrollmentAsync(signature, body));
        Assert.Empty(fixture.Db.GroupMessages); Assert.Empty(fixture.Db.GroupListenerLeases);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"source\":null,\"identity\":null}")]
    [InlineData("{\"source\":null,\"source\":null,\"identity\":null}")]
    [InlineData("{\"source\":null,\"identity\":null,\"secretRef\":\"PRIVATE_FIXTURE\"}")]
    public void MalformedMetadataCannotChooseAuthorityOrCredential(string json)
    { Assert.Throws<UnauthorizedAccessException>(() => GroupServiceAuthenticator.ParseEnrollment(Encoding.UTF8.GetBytes(json))); }

    [Theory]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("external-padding")]
    [InlineData("grant")]
    [InlineData("epoch")]
    [InlineData("account-version")]
    [InlineData("profile")]
    public async Task UnusableMetadataResponseNeverBecomesCurrentEnrollment(string change)
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var body = Body(fixture);
        var snapshot = await fixture.Authenticator.AuthenticateEnrollmentAsync(Sign(fixture, body), body);
        snapshot = change switch
        {
            "future" => snapshot with { CheckedAtUtc = fixture.Clock.Current.AddTicks(1) },
            "stale" => snapshot with { CheckedAtUtc = fixture.Clock.Current.AddSeconds(-10).AddTicks(-1) },
            "foreign" => snapshot with { Source = snapshot.Source with { Scope = snapshot.Source.Scope with { SourceBindingId = Guid.NewGuid() } } },
            "external-padding" => snapshot with { Source = snapshot.Source with { ExternalIdentity = snapshot.Source.ExternalIdentity with { GroupId = fixture.External.GroupId.TrimEnd() } } },
            "grant" => snapshot with { Grant = snapshot.Grant with { IsEnabled = false } },
            "epoch" => snapshot with { Authentication = snapshot.Authentication with { CredentialEpoch = 2 } },
            "account-version" => snapshot with { AccountVersion = 0 },
            _ => snapshot with { Environment = GroupQualificationEnvironment.ControlledAccount }
        };
        using var client = Client(fixture, (http, _) => Task.FromResult(Reply(http, snapshot)));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.FetchEnrollmentAsync(new(fixture.Scope, fixture.External)));
    }

    [Fact]
    public async Task PreparedMetadataCannotEnterEitherCommitDomainAndForeignHostScopeIsPrekeyDenied()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var sends = 0;
        using var client = Client(fixture, (_, _) => { sends++; throw new IOException("PRIVATE_FIXTURE"); });
        using var prepared = await client.PrepareEnrollmentAsync(new(fixture.Scope, fixture.External));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.SendEventAsync(prepared));
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.SendListenerAsync(prepared));
        Assert.Equal(0, sends); var keys = fixture.Secrets.Calls;
        await Assert.ThrowsAsync<GroupConnectorTransportException>(() => client.PrepareEnrollmentAsync(new(fixture.Scope with { CompanyId = Guid.NewGuid() }, fixture.External)));
        Assert.Equal(keys, fixture.Secrets.Calls);
    }

    [Fact]
    public async Task ControlledLiveMetadataAndExactTenSecondAgeBoundaryRemainPositive()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(controlled: true);
        var body = Body(fixture);
        var snapshot = await fixture.Authenticator.AuthenticateEnrollmentAsync(Sign(fixture, body), body);
        snapshot = snapshot with { CheckedAtUtc = fixture.Clock.Current.AddSeconds(-10) };
        using var client = Client(fixture, (http, _) => Task.FromResult(Reply(http, snapshot)), live: true);
        var current = await client.FetchEnrollmentAsync(new(fixture.Scope, fixture.External));
        Assert.Equal(GroupQualificationEnvironment.ControlledAccount, current.Enrollment.Qualification.Environment);
        Assert.Equal(fixture.Clock.Current.AddSeconds(-10), current.CheckedAtUtc);
    }

    private static byte[] Body(GroupServiceAuthenticatorTests.Fixture fixture) =>
        JsonSerializer.SerializeToUtf8Bytes(new GroupConnectorEnrollmentRequest(fixture.Scope, fixture.External), GroupServiceAuthenticator.JsonOptions);
    private static GroupServiceSignature Sign(GroupServiceAuthenticatorTests.Fixture fixture, byte[] body)
    {
        var signature = fixture.Sign(body: body);
        return signature with { SignatureHex = Convert.ToHexString(HMACSHA256.HashData(fixture.Secrets.Key, GroupServiceAuthenticator.EnrollmentSigningBytes(signature, body))) };
    }
    private static GroupConnectorTransportClient Client(GroupServiceAuthenticatorTests.Fixture fixture,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, bool live = false) => new(new(live ? "https://owned.invalid/" : "http://127.0.0.1/"),
        new(fixture.Scope.TenantId, fixture.Scope.CompanyId, fixture.Service.Id, 1, SecretReference.Parse("secretref://env/OWNED_GROUP_KEY")),
        new([fixture.Secrets]), fixture.Clock, live ? GroupIngressRuntimePolicy.Live : GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), new Handler(handler));
    private static GroupServiceSignature Signature(HttpRequestMessage request) => new(
        Guid.Parse(request.Headers.GetValues("X-AIOffice-Group-Service").Single()),
        long.Parse(request.Headers.GetValues("X-AIOffice-Group-Epoch").Single(), CultureInfo.InvariantCulture),
        long.Parse(request.Headers.GetValues("X-AIOffice-Group-Signed-At").Single(), CultureInfo.InvariantCulture),
        Guid.Parse(request.Headers.GetValues("X-AIOffice-Group-Nonce").Single()), request.Headers.GetValues("X-AIOffice-Group-Signature").Single());
    private static HttpResponseMessage Reply(HttpRequestMessage request, object value) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new StringContent(JsonSerializer.Serialize(value, GroupServiceAuthenticator.JsonOptions), Encoding.UTF8, "application/json"),
        Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken); }
}
