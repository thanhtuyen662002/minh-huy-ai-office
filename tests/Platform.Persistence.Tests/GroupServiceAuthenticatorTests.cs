using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupServiceAuthenticatorTests
{
    [Fact]
    public async Task SignedOwnedSourceDerivesScopeAndCapturesCurrentAuthorityWithoutPortalUser()
    {
        using var fixture = new Fixture();
        var verified = await fixture.Authenticator.AuthenticateAsync(fixture.Sign(), fixture.Body());
        Assert.Equal(fixture.Scope, verified.Source); Assert.Equal(1, fixture.Secrets.Calls);
        Assert.Equal("original 😀\uFEFF ", verified.Payload.Text);
        Assert.Empty(await fixture.Db.Tasks.ToListAsync());
        Assert.Empty(await fixture.Db.CompanyMemberships.ToListAsync());
        await new GroupServiceDirectory(fixture.Db).RequireCurrentAsync(verified.Service, default);
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
    public async Task UnenrolledDisabledForeignAndWrongCapabilitySourcesRefuseBeforeCredentialResolution(string change)
    {
        using var fixture = new Fixture(change);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateAsync(fixture.Sign(), fixture.Body()));
        Assert.Equal(0, fixture.Secrets.Calls); Assert.Empty(await fixture.Db.GroupMessages.ToListAsync());
    }

    [Theory]
    [InlineData("source-version")]
    [InlineData("deletion")]
    [InlineData("account-version")]
    [InlineData("grant-version")]
    [InlineData("credential-reference")]
    public async Task FinalCurrentFenceRefusesAnyChangedSnapshot(string change)
    {
        using var fixture = new Fixture();
        var verified = await fixture.Authenticator.AuthenticateAsync(fixture.Sign(), fixture.Body());
        switch (change)
        {
            case "source-version": fixture.Binding.Version++; break;
            case "deletion": fixture.Binding.DeletionGeneration++; break;
            case "account-version": fixture.Account.Version++; break;
            case "grant-version": fixture.Grant.Version++; break;
            case "credential-reference": fixture.Service.CredentialReference = "secretref://env/OTHER_OWNED_KEY"; break;
        }
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GroupServiceDirectory(fixture.Db).RequireCurrentAsync(verified.Service, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalPhysicalRoleCatalogRefusesEvenDisabledForeignAliasWithForgedIndex(bool enabled)
    {
        using var fixture = new Fixture();
        fixture.Db.GroupBindings.Add(new()
        {
            TenantId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            Id = Guid.NewGuid(),
            ConnectorAccountId = Guid.NewGuid(),
            Provider = fixture.External.Provider,
            ExternalAccountId = "other-account",
            ExternalGroupId = fixture.External.GroupId,
            IdentityHash = new GroupExternalIdentity(fixture.External.Provider, "other-account", fixture.External.GroupId).IndexKey(),
            PhysicalGroupHash = new string('1', 64),
            Role = GroupBindingRole.TechnicalInternal,
            IsEnabled = enabled,
            DisplayName = "Other fixture"
        });
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateAsync(fixture.Sign(), fixture.Body()));
        Assert.Equal(0, fixture.Secrets.Calls);
    }

    [Theory]
    [InlineData("group😀")]
    [InlineData("Group😀 ")]
    public async Task PhysicalCatalogKeepsCaseAndTrailingPaddingAsDistinctOriginalIdentities(string otherGroup)
    {
        using var fixture = new Fixture();
        fixture.Db.GroupBindings.Add(new()
        {
            TenantId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            Id = Guid.NewGuid(),
            ConnectorAccountId = Guid.NewGuid(),
            Provider = fixture.External.Provider,
            ExternalAccountId = "other-account",
            ExternalGroupId = otherGroup,
            IdentityHash = new GroupExternalIdentity(fixture.External.Provider, "other-account", otherGroup).IndexKey(),
            PhysicalGroupHash = GroupIngressIdentity.PhysicalGroupIndex(fixture.External.Provider, otherGroup),
            Role = GroupBindingRole.TechnicalInternal,
            IsEnabled = true,
            DisplayName = "Other fixture"
        });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(fixture.Scope, (await fixture.Authenticator.AuthenticateAsync(fixture.Sign(), fixture.Body())).Source);
    }

    [Fact]
    public async Task SignatureBindsExactBodyNonceServiceEpochAndTimestamp()
    {
        using var fixture = new Fixture(); var signature = fixture.Sign();
        foreach (var changed in new[] { signature with { Nonce = Guid.NewGuid() }, signature with { ServiceId = Guid.NewGuid() },
            signature with { CredentialEpoch = 2 }, signature with { SignedAtUnixSeconds = signature.SignedAtUnixSeconds - 1 },
            signature with { SignatureHex = new string('0', 64) }, signature with { SignatureHex = signature.SignatureHex.ToLowerInvariant() } })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateAsync(changed, fixture.Body()));
        var changedBody = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.Body()).Replace("original", "changed", StringComparison.Ordinal));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateAsync(signature, changedBody));
        await fixture.Authenticator.AuthenticateAsync(signature, fixture.Body());
    }

    [Fact]
    public async Task ExactClockSkewBoundaryAcceptedAdjacentAndUnrepresentableTimestampDenied()
    {
        using var fixture = new Fixture();
        foreach (var skew in new[] { -120, 120 })
            await fixture.Authenticator.AuthenticateAsync(fixture.Sign(skew), fixture.Body());
        foreach (var skew in new[] { -121, 121 })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateAsync(fixture.Sign(skew), fixture.Body()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateAsync(fixture.Sign() with { SignedAtUnixSeconds = long.MaxValue }, fixture.Body()));
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("self")]
    [InlineData("echo")]
    [InlineData("unenrolled")]
    public async Task CustomerDmSelfEchoAndUnenrolledEventsNeverReachStorageOrCredentials(string change)
    {
        using var fixture = new Fixture(); var payload = fixture.Payload();
        payload = change switch
        {
            "dm" => payload with { IsGroup = false },
            "self" => payload with { IsSelf = true },
            "echo" => payload with { IsKnownReportEcho = true },
            _ => payload with { Event = payload.Event with { Identity = payload.Event.Identity with { GroupId = "other" } } }
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, GroupServiceAuthenticator.JsonOptions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateAsync(fixture.Sign(body: body), body));
        Assert.Equal(0, fixture.Secrets.Calls); Assert.Empty(await fixture.Db.GroupIngressReceipts.ToListAsync());
    }

    [Fact]
    public async Task LivePolicyCannotPromoteSyntheticQualificationAndFixtureRequiresOwnedDevelopmentHost()
    {
        using var fixture = new Fixture();
        var live = fixture.CreateAuthenticator(GroupIngressRuntimePolicy.Live);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => live.AuthenticateAsync(fixture.Sign(), fixture.Body()));
        Assert.Equal(0, fixture.Secrets.Calls);
        Assert.Throws<InvalidOperationException>(() => GroupIngressRuntimePolicy.OwnedSyntheticFixture("Production", true));
        Assert.Throws<InvalidOperationException>(() => GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", false));
        await fixture.Authenticator.AuthenticateAsync(fixture.Sign(), fixture.Body());
    }

    [Fact]
    public void StrictBodyRejectsUnknownNestedAuthorityDuplicateDecodedFieldsMissingFlagsAndInvalidBytes()
    {
        using var fixture = new Fixture(); var text = Encoding.UTF8.GetString(fixture.Body());
        foreach (var changed in new[]
        {
            text.Replace("\"text\":", "\"mode\":\"synthetic\",\"text\":", StringComparison.Ordinal),
            text.Replace("\"provider\":", "\"tenantId\":\"fake\",\"provider\":", StringComparison.Ordinal),
            text.Replace("\"text\":", "\"t\\u0065xt\":\"other\",\"text\":", StringComparison.Ordinal),
            text.Replace("\"isGroup\":true,", "", StringComparison.Ordinal),
            text.Replace("\"senderId\":\"sender\"", "\"senderId\":\"\\ud800\"", StringComparison.Ordinal)
        }) Assert.Throws<UnauthorizedAccessException>(() => GroupServiceAuthenticator.Parse(Encoding.UTF8.GetBytes(changed)));
        Assert.Throws<UnauthorizedAccessException>(() => GroupServiceAuthenticator.Parse(new byte[] { 0xFF }));
        Assert.Throws<UnauthorizedAccessException>(() => GroupServiceAuthenticator.Parse(new byte[65537]));
        Assert.Equal(fixture.Payload(), GroupServiceAuthenticator.Parse(fixture.Body()));
    }

    [Fact]
    public void OriginalSqlUtf16DecoderRejectsInvalidStoredBytesButPreservesLiteralReplacementAndPadding()
    {
        foreach (var text in new[] { "account ", "\uFFFD", "\uFEFF😀" })
            Assert.Equal(text, GroupRegistryReader.Decode(Encoding.Unicode.GetBytes(text), 512));
        foreach (var bytes in new[] { new byte[] { 0, 0xD8 }, new byte[] { 0, 0xDC }, new byte[] { 0 }, new byte[514] })
            Assert.Throws<UnauthorizedAccessException>(() => GroupRegistryReader.Decode(bytes, 512));
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly DbContextOptions<PlatformDbContext> Options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        internal readonly PlatformDbContext Db;
        internal readonly GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        internal readonly GroupExternalIdentity External = new("synthetic", "account ", "group😀 ");
        internal readonly GroupServiceRecord Service;
        internal readonly GroupConnectorAccountRecord Account;
        internal readonly GroupBindingRecord Binding;
        internal readonly GroupServiceGrantRecord Grant;
        internal readonly OwnedSecrets Secrets = new();
        internal GroupServiceAuthenticator Authenticator => CreateAuthenticator(GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true));
        internal static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        internal Fixture(string? change = null)
        {
            Db = new(Options);
            var company = new CompanyRecord { TenantId = Scope.TenantId, Id = Scope.CompanyId, Code = "owned", Name = "Owned fixture", IsActive = change != "company" };
            Account = new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                Id = Guid.NewGuid(),
                Provider = External.Provider,
                ExternalAccountId = External.AccountId,
                IdentityHash = GroupIngressIdentity.AccountIndex(External.Provider, External.AccountId),
                PackageVersion = "owned-fixture",
                GitCommit = new string('0', 40),
                QualificationJson = "{\"environment\":1,\"observations\":[]}",
                IsEnabled = true
            };
            Service = new() { TenantId = Scope.TenantId, CompanyId = Scope.CompanyId, Id = Guid.NewGuid(), CredentialReference = "secretref://env/OWNED_GROUP_KEY", IsEnabled = true };
            Binding = new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                Id = Scope.SourceBindingId,
                ConnectorAccountId = Account.Id,
                Provider = External.Provider,
                ExternalAccountId = External.AccountId,
                ExternalGroupId = External.GroupId,
                IdentityHash = External.IndexKey(),
                PhysicalGroupHash = GroupIngressIdentity.PhysicalGroupIndex(External.Provider, External.GroupId),
                Role = GroupBindingRole.CustomerSource,
                DisplayName = "Owned source",
                IsEnabled = true
            };
            Grant = new() { TenantId = Scope.TenantId, CompanyId = Scope.CompanyId, ServiceId = Service.Id, BindingId = Binding.Id, Capability = GroupServiceCapability.Ingest, IsEnabled = true };
            switch (change)
            {
                case "source": Binding.IsEnabled = false; break;
                case "role": Binding.Role = GroupBindingRole.TechnicalInternal; break;
                case "service": Service.IsEnabled = false; break;
                case "epoch": Service.CredentialEpoch = 2; break;
                case "account": Account.IsEnabled = false; break;
                case "account-alias": Account.ExternalAccountId = External.AccountId.TrimEnd(); break;
                case "hash": Binding.PhysicalGroupHash = new string('0', 64); break;
                case "grant": Grant.IsEnabled = false; break;
                case "capability": Grant.Capability = GroupServiceCapability.Extract; break;
            }
            Db.AddRange(company, Account, Service, Binding, Grant); Db.SaveChanges();
        }
        internal GroupServiceAuthenticator CreateAuthenticator(GroupIngressRuntimePolicy policy) => new(Db, new([Secrets]), policy, new OwnedClock());
        internal GroupIngressPayload Payload() => new(new(External, "message", "revision", "sender", null, GroupSourceEventKind.NewText,
            Now, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("original 😀\uFEFF "))), false), "original 😀\uFEFF ", true, false, false,
            Guid.Parse("10000000-0000-0000-0000-000000000001"), 1);
        internal byte[] Body() => JsonSerializer.SerializeToUtf8Bytes(Payload(), GroupServiceAuthenticator.JsonOptions);
        internal GroupServiceSignature Sign(int skew = 0, byte[]? body = null)
        {
            var signature = new GroupServiceSignature(Service.Id, 1, Now.ToUnixTimeSeconds() + skew,
                Guid.Parse("10000000-0000-0000-0000-000000000002"), "");
            return signature with { SignatureHex = Convert.ToHexString(HMACSHA256.HashData(Secrets.Key, GroupServiceAuthenticator.SigningBytes(signature, body ?? Body()))) };
        }
        public void Dispose() => Db.Dispose();
    }
    internal sealed class OwnedSecrets : ISecretResolver
    {
        internal readonly byte[] Key = Enumerable.Repeat((byte)0x31, 32).ToArray();
        internal int Calls;
        public string Provider => "env";
        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        { Assert.Equal("secretref://env/OWNED_GROUP_KEY", reference.Value); Calls++; return ValueTask.FromResult(Convert.ToBase64String(Key)); }
    }
    private sealed class OwnedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Fixture.Now; }
}
