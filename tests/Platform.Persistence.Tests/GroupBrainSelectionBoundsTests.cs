using System.Collections;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private sealed class BoundedProbeIds(int actualCount) : IReadOnlyList<Guid>
    {
        public int Count => 1;
        public Guid this[int index] => throw new NotSupportedException();
        internal int Enumerated;
        public IEnumerator<Guid> GetEnumerator()
        {
            for (var index = 0; index < actualCount; index++)
            {
                if (++Enumerated > GroupBrainCurrentReader.MaximumSelectedRevisions + 1)
                    throw new InvalidOperationException("Selection enumerated past its sentinel.");
                yield return Guid.NewGuid();
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrainSelectionStopsAtOverflowSentinelBeforeAnyKeys(bool glossary)
    {
        using var f = new Fixture(); await f.CommitAsync(); var claim = await f.ClaimAsync();
        var ids = new BoundedProbeIds(int.MaxValue);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f)
            .ReadAsync(claim, glossary ? [] : ids, glossary ? ids : []));
        Assert.Equal("Group brain context is unavailable.", error.Message);
        Assert.Equal(21, ids.Enumerated); Assert.Equal(0, f.Keys.Reads);
    }

    [Fact]
    public async Task BrainSelectionChecksCombinedCopiedLengthBeforeAnyKeys()
    {
        using var f = new Fixture(); await f.CommitAsync(); var claim = await f.ClaimAsync();
        var requests = new BoundedProbeIds(11); var glossary = new BoundedProbeIds(10);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, requests, glossary));
        Assert.Equal(11, requests.Enumerated); Assert.Equal(10, glossary.Enumerated); Assert.Equal(0, f.Keys.Reads);
    }
}
