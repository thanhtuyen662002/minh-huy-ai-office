using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Configuration.Tests;

public sealed class ProtectedSecretEncryptionTests
{
    private static ProtectedSecretEncryption Encryption() => new(() => Convert.ToBase64String(new byte[32]));
    [Fact]
    public void RoundTripUsesRandomNoncesAndNeverStoresPlaintext()
    {
        var crypto = Encryption();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid(); var binding = Guid.NewGuid();
        var first = crypto.Encrypt(tenant, company, binding, "synthetic-password");
        var second = crypto.Encrypt(tenant, company, binding, "synthetic-password");
        Assert.Equal("synthetic-password", crypto.Decrypt(tenant, company, binding, first));
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.DoesNotContain("synthetic-password", Encoding.UTF8.GetString(first.Ciphertext));
        Assert.Equal(12, first.Nonce.Length); Assert.Equal(16, first.Tag.Length);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("ciphertext")]
    [InlineData("nonce")]
    [InlineData("tag")]
    [InlineData("key")]
    public void TamperingOrReplayingUnderAnotherScopeFailsClosed(string mode)
    {
        var crypto = Encryption();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid(); var binding = Guid.NewGuid();
        var payload = crypto.Encrypt(tenant, company, binding, "synthetic-password");
        if (mode == "tenant") tenant = Guid.NewGuid();
        if (mode == "company") company = Guid.NewGuid();
        if (mode == "binding") binding = Guid.NewGuid();
        if (mode == "ciphertext") payload.Ciphertext[0] ^= 1;
        if (mode == "nonce") payload.Nonce[0] ^= 1;
        if (mode == "tag") payload.Tag[0] ^= 1;
        if (mode == "key") crypto = new(() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var error = Assert.Throws<InvalidOperationException>(() => crypto.Decrypt(tenant, company, binding, payload));
        Assert.Equal("Protected credentials are unavailable.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    public void MissingOrMalformedKeyRefusesEncryptionAndFingerprint(string? key)
    {
        var crypto = new ProtectedSecretEncryption(() => key);
        Assert.Throws<InvalidOperationException>(() => crypto.Encrypt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic"));
        Assert.Throws<InvalidOperationException>(() => crypto.Fingerprint("synthetic"));
    }
}
