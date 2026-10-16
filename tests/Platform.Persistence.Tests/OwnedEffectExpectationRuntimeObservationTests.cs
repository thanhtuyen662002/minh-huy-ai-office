extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedEffectExpectationRuntimeObservationTests
{
    [Fact]
    public void ObserverCopiesOnlyStoredDigestAndDoesNotMistakeCarrierShapeForCompleteGraphProof()
    {
        var receipt = new GroupWorkCommitReceiptRecord { DependencyManifestVersion = 1, EffectLedgerVersion = 1, ExpectedEffectSha256 = Enumerable.Repeat((byte)0xAB, 32).ToArray() };
        var value = GroupAutomaticEffectExpectationRuntimeProof.RequireValue(receipt);
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)), value);
        receipt.ExpectedEffectSha256[0] ^= 1;
        Assert.NotEqual(value, GroupAutomaticEffectExpectationRuntimeProof.RequireValue(receipt));
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)), value);
        // Random well-shaped bytes are only a carrier; SQL callers also require
        // original replay/full graphs and the shipping stored-digest comparison.
    }

    [Theory]
    [InlineData(0, -1, 1)]
    [InlineData(0, 32, 1)]
    [InlineData(2, 32, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 31, 1)]
    [InlineData(1, 33, 1)]
    [InlineData(1, 32, 0)]
    [InlineData(1, 32, 2)]
    public void MissingLegacyMalformedExpectationOrDependencyRefuses(int version, int length, int dependency)
    {
        var receipt = new GroupWorkCommitReceiptRecord
        {
            DependencyManifestVersion = dependency,
            EffectLedgerVersion = version,
            ExpectedEffectSha256 = length < 0 ? null : Enumerable.Repeat((byte)1, length).ToArray()
        };
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticEffectExpectationRuntimeProof.RequireValue(receipt));
    }

    [Fact]
    public void ZeroDigestPlaceholderCannotQualifyOwnedNativeObservation()
    {
        var receipt = new GroupWorkCommitReceiptRecord { DependencyManifestVersion = 1, EffectLedgerVersion = 1, ExpectedEffectSha256 = new byte[32] };
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticEffectExpectationRuntimeProof.RequireValue(receipt));
    }
}
