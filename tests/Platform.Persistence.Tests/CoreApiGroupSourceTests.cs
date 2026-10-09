using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class CoreApiDataSourceAuthorizationIntegrationTests
{
    [Fact]
    public async Task GroupReadInitialDirectoryPreservesCallerAbortWithoutSuccessOrPrivateKeyWork()
    {
        await using var fixture = new GroupReadApiFixture(); var receipt = await fixture.CommitAsync();
        await using var stalled = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAuthenticatedAuthorizationDirectory>();
            services.AddSingleton<IAuthenticatedAuthorizationDirectory>(new GroupInitialDirectoryFailure(true));
        }));
        using var client = stalled.CreateClient(); using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var response = await stalled.Server.SendAsync(context =>
        {
            context.Request.Method = "GET"; context.Request.Path = fixture.Path(receipt.MessageId);
            context.Request.Headers[AuthorizationHeaders.CompanyId] = fixture.Authority.CompanyId.ToString("D");
        }, abort.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(abort.IsCancellationRequested); Assert.Equal(499, response.Response.StatusCode);
        using var reader = new StreamReader(response.Response.Body);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadToEndAsync()); Assert.Equal(0, fixture.Keys.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupReadInitialIdentityDirectorySqlFailureOrStallRemainsBoundedNoStore(bool stall)
    {
        await using var fixture = new GroupReadApiFixture(); var receipt = await fixture.CommitAsync();
        await using var broken = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAuthenticatedAuthorizationDirectory>();
            services.AddSingleton<IAuthenticatedAuthorizationDirectory>(new GroupInitialDirectoryFailure(stall));
        }));
        using var client = broken.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, fixture.Authority.CompanyId.ToString("D"));
        using var response = await client.GetAsync(fixture.Path(receipt.MessageId)).WaitAsync(TimeSpan.FromSeconds(15));
        await GroupReadBoundedAsync(response, HttpStatusCode.ServiceUnavailable); Assert.Equal(0, fixture.Keys.Reads);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("member")]
    public async Task ExplicitGroupReaderGrantWorksForCurrentMemberWithoutAdministrationRole(string role)
    {
        await using var fixture = new GroupReadApiFixture(role); var receipt = await fixture.CommitAsync();
        using var client = fixture.Client(); using var response = await client.GetAsync(fixture.Path(receipt.MessageId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(fixture.Auth.Payload().Text, (await response.Content.ReadFromJsonAsync<GroupSourceMessageView>())!.Text);
    }

    [Fact]
    public async Task GroupSourceGetUsesCurrentMemberAndExplicitReaderGrantWithoutTaskOwnership()
    {
        await using var fixture = new GroupReadApiFixture(); var receipt = await fixture.CommitAsync();
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString("D"));
        client.DefaultRequestHeaders.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString("D"));
        using var response = await client.GetAsync(fixture.Path(receipt.MessageId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var view = (await response.Content.ReadFromJsonAsync<GroupSourceMessageView>())!;
        Assert.Equal(fixture.Auth.Scope, view.Source); Assert.Equal(fixture.Auth.Payload().Text, view.Text);
        Assert.Equal(fixture.Auth.Payload().Event.MessageId, view.ExternalMessageId); Assert.Equal(1, fixture.Keys.Reads);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secretref", body); Assert.DoesNotContain("contentKey", body); Assert.DoesNotContain("protectedContent", body);
        Assert.Empty(await fixture.Auth.Db.Tasks.ToArrayAsync());
        Assert.Single(await fixture.Auth.Db.GroupIngressReceipts.ToArrayAsync());
        using var missing = await client.GetAsync(fixture.Path(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); Assert.True(missing.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("user")]
    [InlineData("source")]
    [InlineData("other-source")]
    [InlineData("other-company")]
    public async Task GroupSourceHttpCannotReplaceCurrentScopedReadGrantWithAdminOrHeaders(string change)
    {
        await using var fixture = new GroupReadApiFixture(); var receipt = await fixture.CommitAsync();
        var path = fixture.Path(receipt.MessageId); using var client = fixture.Client();
        switch (change)
        {
            case "grant": fixture.Grant.IsEnabled = false; break;
            case "membership": (await fixture.Auth.Db.CompanyMemberships.SingleAsync()).IsActive = false; break;
            case "user": (await fixture.Auth.Db.Users.SingleAsync()).IsActive = false; break;
            case "source": fixture.Auth.Binding.IsEnabled = false; break;
            case "other-source": path = $"/api/group-sources/{Guid.NewGuid():D}/messages/{receipt.MessageId:D}"; break;
            case "other-company": client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString("D")); break;
        }
        await fixture.Auth.Db.SaveChangesAsync();
        using var response = await client.GetAsync(path); await GroupReadBoundedAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(0, fixture.Keys.Reads);
    }

    [Theory]
    [InlineData("?tenantId=foreign")]
    [InlineData("?userId=foreign")]
    [InlineData("?companyId=foreign")]
    [InlineData("?sourceId=foreign")]
    public async Task GroupReadRejectsRequestAuthoritySelectorsBeforePrivateKeyResolution(string query)
    {
        await using var fixture = new GroupReadApiFixture(); var receipt = await fixture.CommitAsync(); using var client = fixture.Client();
        using var response = await client.GetAsync(fixture.Path(receipt.MessageId) + query);
        await GroupReadBoundedAsync(response, HttpStatusCode.BadRequest); Assert.Equal(0, fixture.Keys.Reads);
    }

    [Fact]
    public async Task GroupReadRejectsNoncanonicalAndEmptyRouteIdentities()
    {
        await using var fixture = new GroupReadApiFixture(); var receipt = await fixture.CommitAsync(); using var client = fixture.Client();
        foreach (var source in new[] { Guid.Empty.ToString("D"), "AAAAAAAA-1111-4111-8111-111111111111", fixture.Auth.Scope.SourceBindingId.ToString("N") })
        {
            using var response = await client.GetAsync($"/api/group-sources/{source}/messages/{receipt.MessageId:D}");
            await GroupReadBoundedAsync(response, HttpStatusCode.BadRequest);
        }
        Assert.Equal(0, fixture.Keys.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupReadDiscardsPrivateResultAfterKeyFailureOrCurrentGrantLoss(bool revoke)
    {
        await using var fixture = new GroupReadApiFixture(); var receipt = await fixture.CommitAsync();
        fixture.Keys.BeforeRead = async () =>
        {
            if (!revoke) throw new InvalidOperationException("PRIVATE_KEY_FAILURE");
            fixture.Grant.IsEnabled = false; await fixture.Auth.Db.SaveChangesAsync();
        };
        using var client = fixture.Client(); using var response = await client.GetAsync(fixture.Path(receipt.MessageId));
        await GroupReadBoundedAsync(response, revoke ? HttpStatusCode.Forbidden : HttpStatusCode.ServiceUnavailable);
        fixture.Keys.BeforeRead = null; fixture.Grant.IsEnabled = true; await fixture.Auth.Db.SaveChangesAsync();
        using var restored = await client.GetAsync(fixture.Path(receipt.MessageId)); Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task IngressHmacHeaderCannotAuthenticatePortalSourceReads()
    {
        await using var fixture = new GroupReadApiFixture(); var receipt = await fixture.CommitAsync();
        await using var anonymous = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<AuthenticationOptions>(options =>
            { options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme; options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme; })));
        using var client = anonymous.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, fixture.Authority.CompanyId.ToString("D"));
        client.DefaultRequestHeaders.Add("X-AIOffice-Group-Service", fixture.Auth.Service.Id.ToString("D"));
        client.DefaultRequestHeaders.Add("X-AIOffice-Group-Signature", new string('A', 64));
        using var response = await client.GetAsync(fixture.Path(receipt.MessageId));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); Assert.Equal(0, fixture.Keys.Reads);
    }

    [Fact]
    public async Task DefaultOffGroupReadIsUnavailableAndPrivateResponseIsNeverCached()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        { builder.UseSetting("AIOffice:GroupIntake:Enabled", "false"); builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", ""); });
        using var client = factory.CreateClient(); using var response = await client.GetAsync($"/api/group-sources/{Guid.NewGuid():D}/messages/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static async Task GroupReadBoundedAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_", text);
        Assert.DoesNotContain("secretref", text); Assert.DoesNotContain("original", text); Assert.DoesNotContain("Exception", text);
    }

    private sealed class GroupReadApiFixture : IAsyncDisposable
    {
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        internal readonly GroupReadKeys Keys = new();
        internal readonly AuthorizationContext Authority;
        internal readonly GroupReaderGrantRecord Grant;
        private readonly string variable = "AIOFFICE_GROUP_READ_" + Guid.NewGuid().ToString("N");
        internal readonly WebApplicationFactory<Program> Factory;
        internal GroupReadApiFixture(string role = "admin")
        {
            Authority = AuthorizationContext.Create(Auth.Scope.TenantId, Auth.Scope.CompanyId, Guid.NewGuid());
            MemberDirectoryFixture.AddMember(Auth.Db, Authority, Authority.UserId, "Owned reader", roles: [role]);
            Grant = new() { TenantId = Authority.TenantId, CompanyId = Authority.CompanyId, UserId = Authority.UserId, BindingId = Auth.Scope.SourceBindingId, IsEnabled = true };
            Auth.Db.AddRange(Grant, new GroupListenerLeaseRecord
            {
                TenantId = Authority.TenantId,
                CompanyId = Authority.CompanyId,
                ConnectorAccountId = Auth.Account.Id,
                OwnerId = Auth.Payload().ListenerOwnerId,
                Epoch = 1,
                HeartbeatAtUtc = GroupServiceAuthenticatorTests.Fixture.Now,
                ExpiresAtUtc = GroupServiceAuthenticatorTests.Fixture.Now.AddMinutes(2)
            });
            Auth.Db.SaveChanges(); Environment.SetEnvironmentVariable(variable, "owned-read-not-a-real-connection");
            Factory = AuthenticatedFactory(new(Authority, [role]), "secretref://env/" + variable, useRealDirectory: true).WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development"); builder.UseSetting("AIOffice:GroupIntake:Enabled", "true");
                builder.UseSetting("AIOffice:GroupIntake:OwnedSyntheticFixture", "true"); builder.UseSetting("AIOffice:GroupIntake:OwnedDisposableFixture", "true");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<PlatformDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>(); services.RemoveAll<PlatformDbContext>();
                    services.AddSingleton(Auth.Options); services.AddScoped(_ => new PlatformDbContext(Auth.Options));
                    services.RemoveAll<IGroupSourceKeyProvider>(); services.AddSingleton<IGroupSourceKeyProvider>(Keys);
                });
            });
        }
        internal string Path(Guid message) => $"/api/group-sources/{Auth.Scope.SourceBindingId:D}/messages/{message:D}";
        internal HttpClient Client()
        { var client = Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Authority.CompanyId.ToString("D")); return client; }
        internal async Task<GroupIngressCommittedReceipt> CommitAsync()
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(Auth.Payload(), GroupServiceAuthenticator.JsonOptions);
            var verified = await Auth.Authenticator.AuthenticateAsync(Auth.Sign(body: body), body);
            return await new GroupIngressStore(Auth.Db, Keys, new(), GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), Auth.Clock).AcceptAsync(verified);
        }
        public async ValueTask DisposeAsync() { await Factory.DisposeAsync(); Environment.SetEnvironmentVariable(variable, null); Auth.Dispose(); }
    }
    private sealed class GroupReadKeys : IGroupSourceKeyProvider
    {
        private readonly byte[] key = Enumerable.Repeat((byte)0x32, 32).ToArray();
        internal int Reads;
        internal Func<Task>? BeforeRead;
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default) => ValueTask.FromResult(new GroupSourceKeyMaterial("owned", key));
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default)
        { Reads++; if (BeforeRead is not null) await BeforeRead(); return new(keyId, key); }
    }

    private sealed class GroupInitialDirectoryFailure(bool stall) : IAuthenticatedAuthorizationDirectory
    {
        public async Task<AuthenticatedAuthorizationEntry?> ResolveAsync(string identityProvider, string subject, Guid companyId, CancellationToken cancellationToken = default)
        {
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("PRIVATE_INITIAL_DIRECTORY_FAILURE");
        }
    }
}
