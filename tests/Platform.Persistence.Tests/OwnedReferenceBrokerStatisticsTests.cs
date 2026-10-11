extern alias GroupReferenceProof;

using System.Text;
using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedReferenceBrokerStatisticsTests
{
    [Fact]
    public void BrokerSnapshotRequiresTheOriginalExactCountsTogether()
    {
        var held = Decode("{\"message_stats\":{\"ack\":0,\"deliver\":1},\"consumers\":1}");
        var replay = Decode("{\"message_stats\":{\"ack\":1,\"deliver\":2},\"consumers\":1}");
        Assert.True(held.Matches(0, 1, 1)); Assert.True(replay.Matches(1, 2, 1));
        foreach (var wrong in new[] { new OwnedReferenceBrokerStatistics(0, 2, 1), new(1, 1, 1), new(2, 2, 1),
            new(1, 3, 1), new(1, 2, 0), new(1, 2, 2) }) Assert.False(wrong.Matches(1, 2, 1));
        Assert.False(replay.Matches(2, 3, 1));
    }

    [Theory]
    [InlineData("{\"consumers\":-1}")]
    [InlineData("{\"consumers\":1,\"consumers\":1}")]
    [InlineData("{\"consumers\":1,\"message_stats\":{\"ack\":1,\"ack\":1,\"deliver\":2}}")]
    [InlineData("{\"consumers\":1,\"message_stats\":null}")]
    [InlineData("{\"consumers\":1,\"message_stats\":{\"ack\":1.5}}")]
    [InlineData("{\"consumers\":1,\"message_stats\":{\"deliver\":-2}}")]
    [InlineData("{\"consumers\":1,\"message_stats\":{\"ack\":9223372036854775808}}")]
    [InlineData("{\"consumers\":\"1\"}")]
    [InlineData("[]")]
    [InlineData("{\"private\":\"never export decoder input\"")]
    public void MalformedBrokerMetadataHasOnlyFixedPrivateErrors(string body)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Decode(body));
        Assert.Equal("Owned reference statistics unavailable.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void MissingCountersRemainZeroAndCannotManufactureAnAckOrDelivery()
    {
        var absent = Decode("{\"consumers\":1,\"queue_metadata\":\"ignored\"}");
        Assert.Equal(new(0, 0, 1), absent); Assert.False(absent.Matches(1, 2, 1));
        Assert.Throws<InvalidOperationException>(() => OwnedReferenceBrokerStatistics.Parse(new byte[32769]));
        Assert.Throws<InvalidOperationException>(() => OwnedReferenceBrokerStatistics.Parse(ReadOnlyMemory<byte>.Empty));
    }
    private static OwnedReferenceBrokerStatistics Decode(string body) => OwnedReferenceBrokerStatistics.Parse(Encoding.UTF8.GetBytes(body));
}
