using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupListenerAuthenticationTests
{
    private static readonly Guid Owner = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static byte[] Body(GroupServiceAuthenticatorTests.Fixture fixture, GroupListenerCommand? command = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new GroupListenerPayload(fixture.External, command ?? new(Owner, GroupListenerOperation.Acquire, 0)), GroupServiceAuthenticator.JsonOptions);
    private static GroupServiceSignature Sign(GroupServiceAuthenticatorTests.Fixture fixture, byte[] body, bool eventDomain = false)
    {
        var signature = fixture.Sign();
        return signature with
        {
            SignatureHex = Convert.ToHexString(HMACSHA256.HashData(fixture.Secrets.Key, eventDomain
            ? GroupServiceAuthenticator.SigningBytes(signature, body) : GroupServiceAuthenticator.ListenerSigningBytes(signature, body)))
        };
    }

    [Fact]
    public async Task ListenerAuthorityDerivesCurrentAccountWithoutPortalUserOrIntakeEvent()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var body = Body(fixture);
        var verified = await fixture.Authenticator.AuthenticateListenerAsync(Sign(fixture, body), body);
        Assert.Equal(new(fixture.Scope.TenantId, fixture.Scope.CompanyId, fixture.Account.Id), verified.Account);
        Assert.Equal(new(Owner, GroupListenerOperation.Acquire, 0), verified.Command);
        Assert.Empty(fixture.Db.GroupListenerLeases); Assert.Empty(fixture.Db.GroupCoverageGaps);
        Assert.Empty(fixture.Db.GroupIngressReceipts); Assert.Empty(fixture.Db.Tasks); Assert.Empty(fixture.Db.CompanyMemberships);
        await new GroupServiceDirectory(fixture.Db).RequireCurrentAsync(verified.Service, default);
    }

    [Fact]
    public async Task IngestAndListenerSignatureDomainsCannotBeReplayedAcrossCommands()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var body = Body(fixture);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateListenerAsync(Sign(fixture, body, true), body));
        await fixture.Authenticator.AuthenticateListenerAsync(Sign(fixture, body), body);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateAsync(Sign(fixture, body), body));
        var changed = Body(fixture, new(Owner, GroupListenerOperation.Renew, 1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateListenerAsync(Sign(fixture, body), changed));
        await fixture.Authenticator.AuthenticateAsync(fixture.Sign(), fixture.Body());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("role")]
    [InlineData("service")]
    [InlineData("epoch")]
    [InlineData("account")]
    [InlineData("account-alias")]
    [InlineData("grant")]
    [InlineData("capability")]
    public async Task UnqualifiedOrUnenrolledAccountCannotBootstrapOwnershipBeforeCredentials(string change)
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(change); var body = Body(fixture);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateListenerAsync(Sign(fixture, body), body));
        Assert.Equal(0, fixture.Secrets.Calls); Assert.Empty(fixture.Db.GroupListenerLeases);
    }

    [Fact]
    public async Task CallerBodyMutationDuringKeyAwaitCannotAlterOwnerOrSignedSnapshot()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var body = Body(fixture); var signature = Sign(fixture, body);
        fixture.Secrets.BeforeResolution = () => Array.Fill(body, (byte)'x');
        var verified = await fixture.Authenticator.AuthenticateListenerAsync(signature, body);
        Assert.Equal(Owner, verified.Command.OwnerId); Assert.Equal(fixture.External, verified.Service.External);
    }

    [Fact]
    public async Task AuthenticatedAccountIdCannotBeReplacedBySameIdentityAndVersionRegistryRow()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var body = Body(fixture);
        var verified = await fixture.Authenticator.AuthenticateListenerAsync(Sign(fixture, body), body);
        var old = fixture.Account;
        var replacement = new GroupConnectorAccountRecord
        {
            TenantId = old.TenantId,
            CompanyId = old.CompanyId,
            Id = Guid.NewGuid(),
            Provider = old.Provider,
            ExternalAccountId = old.ExternalAccountId,
            IdentityHash = old.IdentityHash,
            PackageVersion = old.PackageVersion,
            GitCommit = old.GitCommit,
            QualificationJson = old.QualificationJson,
            Version = old.Version,
            IsEnabled = true
        };
        fixture.Db.Add(replacement); fixture.Binding.ConnectorAccountId = replacement.Id;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.Remove(old);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(old.Id, verified.Account.ConnectorAccountId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GroupServiceDirectory(fixture.Db).RequireCurrentAsync(verified.Service, default));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("grant")]
    [InlineData("epoch")]
    [InlineData("expired-signature")]
    public async Task FinalCurrentServiceGrantAndSigningTimeStillFenceListenerAuthentication(string change)
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var body = Body(fixture);
        fixture.Secrets.BeforeResolution = () =>
        {
            if (change == "source") fixture.Binding.IsEnabled = false;
            if (change == "grant") fixture.Grant.Version++;
            if (change == "epoch") fixture.Service.CredentialEpoch++;
            if (change == "expired-signature") fixture.Clock.Current = GroupServiceAuthenticatorTests.Fixture.Now.AddSeconds(121);
            fixture.Db.SaveChanges();
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authenticator.AuthenticateListenerAsync(Sign(fixture, body), body));
        Assert.Empty(fixture.Db.GroupListenerLeases);
    }

    [Fact]
    public async Task LiveListenerCannotPromoteOwnedSyntheticAccount()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var body = Body(fixture);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAuthenticator(GroupIngressRuntimePolicy.Live).AuthenticateListenerAsync(Sign(fixture, body), body));
        Assert.Equal(0, fixture.Secrets.Calls);
    }

    [Fact]
    public void ListenerParserRejectsDecodedDuplicatesUnknownAuthorityMissingFieldsAndInvalidUtf8()
    {
        using var fixture = new GroupServiceAuthenticatorTests.Fixture(); var text = Encoding.UTF8.GetString(Body(fixture));
        foreach (var invalid in new[] { text.Replace("\"ownerId\":", "\"ownerId\":\"10000000-0000-0000-0000-000000000004\",\"own\\u0065rId\":", StringComparison.Ordinal),
            text.Replace("\"expectedEpoch\":0", "\"expectedEpoch\":0,\"tenantId\":\"forged\"", StringComparison.Ordinal),
            text.Replace(",\"expectedEpoch\":0", "", StringComparison.Ordinal), text.Replace("\"operation\":1", "\"operation\":99", StringComparison.Ordinal),
            text.Replace("\"expectedEpoch\":0", "\"expectedEpoch\":1", StringComparison.Ordinal), "{}", "null" })
            Assert.Throws<UnauthorizedAccessException>(() => GroupServiceAuthenticator.ParseListener(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<UnauthorizedAccessException>(() => GroupServiceAuthenticator.ParseListener(new byte[] { 0xff }));
        Assert.Throws<UnauthorizedAccessException>(() => GroupServiceAuthenticator.ParseListener(new byte[8193]));
        Assert.Throws<UnauthorizedAccessException>(() => GroupServiceAuthenticator.ParseListener(ReadOnlyMemory<byte>.Empty));
    }
}
