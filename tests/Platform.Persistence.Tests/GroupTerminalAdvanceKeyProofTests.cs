using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupTerminalAdvanceKeyProofTests
{
    [Fact]
    public void FingerprintMatchesIndependentRfcExtractExpandAndIsDetached()
    {
        var scope = Scope(); var bytes = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray(); using var key = new GroupSourceKeyMaterial("owned-key", bytes);
        var salt = Encoding.ASCII.GetBytes(FormattableString.Invariant($"aioffice-group-terminal-enrollment-key-v1\0{scope.TenantId:D}/{scope.CompanyId:D}/{scope.SourceBindingId:D}/owned-key"));
        var extracted = HMACSHA256.HashData(salt, bytes);
        var info = "aioffice-group-terminal-advance-key-enrollment-v1"u8.ToArray(); var block = info.Concat(new byte[] { 1 }).ToArray();
        var expected = SHA256.HashData(HMACSHA256.HashData(extracted, block));
        Assert.Equal(expected, GroupTerminalAdvanceKeyProof.Fingerprint(scope, key));
        var returned = GroupTerminalAdvanceKeyProof.Fingerprint(scope, key); returned[0] ^= 1;
        Assert.Equal(expected, GroupTerminalAdvanceKeyProof.Fingerprint(scope, key));
        CryptographicOperations.ZeroMemory(extracted); CryptographicOperations.ZeroMemory(bytes);
    }

    [Fact]
    public void MaterialIdentitySeparatesEveryScopeKeyIdAndReboundBytes()
    {
        var scope = Scope(); using var key = Key(); var original = GroupTerminalAdvanceKeyProof.Fingerprint(scope, key);
        foreach (var foreign in new[] { scope with { TenantId = Guid.NewGuid() }, scope with { CompanyId = Guid.NewGuid() }, scope with { SourceBindingId = Guid.NewGuid() } })
            Assert.NotEqual(original, GroupTerminalAdvanceKeyProof.Fingerprint(foreign, key));
        using var otherId = new GroupSourceKeyMaterial("other-key", key.Key.ToArray());
        using var rebound = new GroupSourceKeyMaterial(key.KeyId, Enumerable.Repeat((byte)0x99, 32).ToArray());
        Assert.NotEqual(original, GroupTerminalAdvanceKeyProof.Fingerprint(scope, otherId));
        Assert.NotEqual(original, GroupTerminalAdvanceKeyProof.Fingerprint(scope, rebound));
        var row = GroupTerminalAdvanceKeyProof.Stage(Authority(scope), key, Now());
        Private(() => GroupTerminalAdvanceKeyProof.Require(row, scope, rebound));
        Private(() => GroupTerminalAdvanceKeyProof.Require(row, scope, otherId));
        foreach (var foreign in new[] { scope with { TenantId = Guid.NewGuid() }, scope with { CompanyId = Guid.NewGuid() }, scope with { SourceBindingId = Guid.NewGuid() } })
            Private(() => GroupTerminalAdvanceKeyProof.Require(row, foreign, key));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("fingerprint")]
    [InlineData("short")]
    [InlineData("huge")]
    [InlineData("authority")]
    [InlineData("null-authority")]
    [InlineData("null-material")]
    [InlineData("utc")]
    [InlineData("service")]
    [InlineData("credential")]
    [InlineData("grant")]
    [InlineData("source")]
    [InlineData("deletion")]
    [InlineData("account")]
    public void MalformedEnrollmentCarrierRefusesWithoutRebindingOrPrivateDiagnostics(string fault)
    {
        var scope = Scope(); using var key = Key(); var row = GroupTerminalAdvanceKeyProof.Stage(Authority(scope), key, Now());
        switch (fault)
        {
            case "format": row.FormatVersion++; break;
            case "fingerprint": row.MaterialFingerprint[0] ^= 1; break;
            case "short": row.MaterialFingerprint = new byte[31]; break;
            case "huge": row.MaterialFingerprint = new byte[1_000_000]; break;
            case "authority": row.AuthoritySha256 = new byte[32]; break;
            case "null-authority": row.AuthoritySha256 = null!; break;
            case "null-material": row.MaterialFingerprint = null!; break;
            case "utc": row.EnrolledAtUtc = row.EnrolledAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "service": row.ServiceId = Guid.Empty; break;
            case "credential": row.CredentialEpoch = 0; break;
            case "grant": row.GrantVersion = 0; break;
            case "source": row.SourceVersion = 0; break;
            case "deletion": row.DeletionGeneration = -1; break;
            case "account": row.AccountVersion = 0; break;
        }
        Private(() => GroupTerminalAdvanceKeyProof.Require(row, scope, key));
    }

    [Fact]
    public void StagePreservesOriginalActorAcrossLaterRotationAndDoesNotAuthenticateFreshRights()
    {
        var scope = Scope(); using var key = Key(); var initial = Authority(scope);
        var row = GroupTerminalAdvanceKeyProof.Stage(initial, key, Now());
        Assert.Equal(initial.Principal.ServiceId, row.ServiceId); Assert.Equal(initial.Source.DeletionGeneration, row.DeletionGeneration);
        Assert.Equal(Convert.FromHexString(GroupBatchClaimStore.AuthorityFingerprint(initial)), row.AuthoritySha256);
        var later = initial with { Grant = initial.Grant with { Version = 2 }, AccountVersion = 2 };
        var laterRow = GroupTerminalAdvanceKeyProof.Stage(later, key, Now().AddMinutes(1));
        Assert.Equal(row.MaterialFingerprint, laterRow.MaterialFingerprint); Assert.NotEqual(row.AuthoritySha256, laterRow.AuthoritySha256);
        GroupTerminalAdvanceKeyProof.Require(row, scope, key);
        Private(() => GroupTerminalAdvanceKeyProof.Stage(initial with { Grant = initial.Grant with { IsEnabled = false } }, key, Now()));
        Private(() => GroupTerminalAdvanceKeyProof.Stage(initial with { Principal = initial.Principal with { CompanyId = Guid.NewGuid() } }, key, Now()));
        Private(() => GroupTerminalAdvanceKeyProof.Stage(initial, key, Now().ToOffset(TimeSpan.FromHours(7))));
    }

    [Fact]
    public void NullAndDisposedInputsRefusePrivatelyAndMaximumKeyIdRemainsFinite()
    {
        var scope = Scope(); using var key = Key();
        Private(() => GroupTerminalAdvanceKeyProof.Require(null!, scope, key));
        Private(() => GroupTerminalAdvanceKeyProof.Fingerprint(scope, null!));
        Private(() => GroupTerminalAdvanceKeyProof.Fingerprint(scope with { TenantId = Guid.Empty }, key));
        var disposed = Key(); disposed.Dispose(); Private(() => GroupTerminalAdvanceKeyProof.Fingerprint(scope, disposed));
        using var maximum = new GroupSourceKeyMaterial(new string('k', 64), new byte[32]);
        var row = GroupTerminalAdvanceKeyProof.Stage(Authority(scope), maximum, Now());
        Assert.Equal(64, row.KeyId.Length); Assert.Equal(32, row.MaterialFingerprint.Length); GroupTerminalAdvanceKeyProof.Require(row, scope, maximum);
    }

    private static void Private(Action action)
    { var error = Assert.Throws<InvalidOperationException>(action); Assert.Equal("Group terminal key enrollment is not available.", error.Message); Assert.Null(error.InnerException); }
    private static GroupScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static DateTimeOffset Now() => new(2026, 10, 11, 6, 0, 0, TimeSpan.Zero);
    private static GroupSourceKeyMaterial Key() => new("owned-key", Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    private static GroupExtractionAuthority Authority(GroupScope scope) => new(
        new(scope.TenantId, scope.CompanyId, Service, 1, true),
        new(scope.TenantId, scope.CompanyId, Service, scope.SourceBindingId, GroupServiceCapability.Extract, 1, true),
        new(scope, Guid.NewGuid(), new("owned", "account", "source"), "Owned source", 1, 0, true), 1, "secretref://env/OWNED_KEY_COLD");
    private static readonly Guid Service = Guid.NewGuid();
}
