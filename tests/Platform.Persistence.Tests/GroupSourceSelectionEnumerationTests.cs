using System.Collections;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task OriginalSourceSelectionPreservesBothActualIListIds()
    {
        using var f = new Fixture(); var first = await f.CommitAsync();
        var second = await f.CommitAsync(f.Payload(messageId: "second-enumerated", eventId: "second-enumerated-event", text: "Nhập xuất"));
        var claim = await f.ClaimAsync(); var ids = new[] { first.MessageId, second.MessageId };
        var context = await f.Reader.ReadAsync(claim, new FalseSizedBrainIds(ids));
        Assert.Equal(ids, context.Items.Select(x => x.MessageId)); Assert.Equal(1, f.Keys.Reads);
        Assert.Equal(2, GroupBatchSourcePreparation.Create(context).Candidates.Count);
    }

    [Fact]
    public async Task OriginalSourceSelectionRejectsActualIListOverflowBeforeKeys()
    {
        using var f = new Fixture(); await f.CommitAsync(); var claim = await f.ClaimAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim,
            new FalseSizedBrainIds(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray())));
        Assert.Equal(0, f.Keys.Reads);
    }

    [Fact]
    public async Task OriginalSourceSelectionStopsAtActual101AndDisposesWithoutCountOrIndexer()
    {
        using var f = new Fixture(); await f.CommitAsync(); var claim = await f.ClaimAsync();
        var ids = new UnboundedSourceIds();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(claim, ids));
        Assert.Equal("Group batch source context is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(101, ids.Visited); Assert.True(ids.Disposed); Assert.Equal(0, f.Keys.Reads);
    }

    [Fact]
    public async Task OriginalSourceSelectionAcceptsExactly100ActualSources()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var index = 0; index < 100; index++)
            ids.Add((await f.CommitAsync(f.Payload(messageId: $"enumerated-{index}", eventId: $"enumerated-event-{index}", text: "Tồn kho"))).MessageId);
        var context = await f.Reader.ReadAsync(await f.ClaimAsync(), new FalseSizedBrainIds(ids.ToArray()));
        Assert.Equal(ids, context.Items.Select(x => x.MessageId)); Assert.Equal(100, context.Items.Count); Assert.Equal(1, f.Keys.Reads);
    }

    private sealed class UnboundedSourceIds : IReadOnlyList<Guid>
    {
        public int Count => throw new InvalidOperationException("Count must not be read.");
        public Guid this[int index] => throw new InvalidOperationException("Indexer must not be read.");
        public int Visited { get; private set; }
        public bool Disposed { get; private set; }
        public IEnumerator<Guid> GetEnumerator()
        {
            try
            {
                while (true)
                {
                    if (++Visited > 101) throw new InvalidOperationException("Enumeration exceeded the sentinel.");
                    yield return Guid.NewGuid();
                }
            }
            finally { Disposed = true; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
