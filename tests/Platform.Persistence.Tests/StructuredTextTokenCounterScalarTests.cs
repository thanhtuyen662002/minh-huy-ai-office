extern alias RuntimeWorker;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Independent tiktoken0.12 ordinary reference frozen before the scalar repair.
// Original twenty controls remain unchanged in their original test class.
[Collection(nameof(BoundedTokenizationCollection))]
public sealed class StructuredTextTokenCounterScalarTests
{
    public static IEnumerable<object[]> Adjacency()
    {
        yield return new object[] { "category-adjacency-0-0", "\ud801\udc00abc", 5 };
        yield return new object[] { "category-adjacency-0-1", "hello\ud801\udc00World", 6 };
        yield return new object[] { "category-adjacency-0-2", "\ud801\udc00 12 \ud801\udc00", 11 };
        yield return new object[] { "category-adjacency-0-3", "a\ud801\udc00's", 6 };
        yield return new object[] { "category-adjacency-0-4", "\ud801\udc00\ud801\udc00\ud801\udc00a", 13 };
        yield return new object[] { "category-adjacency-0-5", "\ud801\udc00\u00e9\ud801\udc00\u4e2d", 10 };
        yield return new object[] { "category-adjacency-1-0", "\ud801\udc28abc", 5 };
        yield return new object[] { "category-adjacency-1-1", "hello\ud801\udc28World", 6 };
        yield return new object[] { "category-adjacency-1-2", "\ud801\udc28 12 \ud801\udc28", 11 };
        yield return new object[] { "category-adjacency-1-3", "a\ud801\udc28's", 6 };
        yield return new object[] { "category-adjacency-1-4", "\ud801\udc28\ud801\udc28\ud801\udc28a", 13 };
        yield return new object[] { "category-adjacency-1-5", "\ud801\udc28\u00e9\ud801\udc28\u4e2d", 10 };
        yield return new object[] { "category-adjacency-2-0", "\ud835\udc9cabc", 4 };
        yield return new object[] { "category-adjacency-2-1", "hello\ud835\udc9cWorld", 5 };
        yield return new object[] { "category-adjacency-2-2", "\ud835\udc9c 12 \ud835\udc9c", 9 };
        yield return new object[] { "category-adjacency-2-3", "a\ud835\udc9c's", 5 };
        yield return new object[] { "category-adjacency-2-4", "\ud835\udc9c\ud835\udc9c\ud835\udc9ca", 10 };
        yield return new object[] { "category-adjacency-2-5", "\ud835\udc9c\u00e9\ud835\udc9c\u4e2d", 8 };
        yield return new object[] { "category-adjacency-3-0", "\ud835\udcedabc", 4 };
        yield return new object[] { "category-adjacency-3-1", "hello\ud835\udcedWorld", 5 };
        yield return new object[] { "category-adjacency-3-2", "\ud835\udced 12 \ud835\udced", 9 };
        yield return new object[] { "category-adjacency-3-3", "a\ud835\udced's", 5 };
        yield return new object[] { "category-adjacency-3-4", "\ud835\udced\ud835\udced\ud835\udceda", 10 };
        yield return new object[] { "category-adjacency-3-5", "\ud835\udced\u00e9\ud835\udced\u4e2d", 8 };
        yield return new object[] { "category-adjacency-4-0", "\ud842\udfb7abc", 5 };
        yield return new object[] { "category-adjacency-4-1", "hello\ud842\udfb7World", 6 };
        yield return new object[] { "category-adjacency-4-2", "\ud842\udfb7 12 \ud842\udfb7", 11 };
        yield return new object[] { "category-adjacency-4-3", "a\ud842\udfb7's", 6 };
        yield return new object[] { "category-adjacency-4-4", "\ud842\udfb7\ud842\udfb7\ud842\udfb7a", 13 };
        yield return new object[] { "category-adjacency-4-5", "\ud842\udfb7\u00e9\ud842\udfb7\u4e2d", 10 };
        yield return new object[] { "category-adjacency-5-0", "\ud840\udc00abc", 4 };
        yield return new object[] { "category-adjacency-5-1", "hello\ud840\udc00World", 5 };
        yield return new object[] { "category-adjacency-5-2", "\ud840\udc00 12 \ud840\udc00", 9 };
        yield return new object[] { "category-adjacency-5-3", "a\ud840\udc00's", 5 };
        yield return new object[] { "category-adjacency-5-4", "\ud840\udc00\ud840\udc00\ud840\udc00a", 10 };
        yield return new object[] { "category-adjacency-5-5", "\ud840\udc00\u00e9\ud840\udc00\u4e2d", 8 };
        yield return new object[] { "category-adjacency-6-0", "\ud83d\udce6abc", 3 };
        yield return new object[] { "category-adjacency-6-1", "hello\ud83d\udce6World", 4 };
        yield return new object[] { "category-adjacency-6-2", "\ud83d\udce6 12 \ud83d\udce6", 6 };
        yield return new object[] { "category-adjacency-6-3", "a\ud83d\udce6's", 5 };
        yield return new object[] { "category-adjacency-6-4", "\ud83d\udce6\ud83d\udce6\ud83d\udce6a", 7 };
        yield return new object[] { "category-adjacency-6-5", "\ud83d\udce6\u00e9\ud83d\udce6\u4e2d", 6 };
        yield return new object[] { "category-adjacency-7-0", "\ud83d\udc69abc", 3 };
        yield return new object[] { "category-adjacency-7-1", "hello\ud83d\udc69World", 4 };
        yield return new object[] { "category-adjacency-7-2", "\ud83d\udc69 12 \ud83d\udc69", 6 };
        yield return new object[] { "category-adjacency-7-3", "a\ud83d\udc69's", 5 };
        yield return new object[] { "category-adjacency-7-4", "\ud83d\udc69\ud83d\udc69\ud83d\udc69a", 7 };
        yield return new object[] { "category-adjacency-7-5", "\ud83d\udc69\u00e9\ud83d\udc69\u4e2d", 6 };
        yield return new object[] { "category-adjacency-8-0", "\ud83e\udee0abc", 4 };
        yield return new object[] { "category-adjacency-8-1", "hello\ud83e\udee0World", 5 };
        yield return new object[] { "category-adjacency-8-2", "\ud83e\udee0 12 \ud83e\udee0", 8 };
        yield return new object[] { "category-adjacency-8-3", "a\ud83e\udee0's", 6 };
        yield return new object[] { "category-adjacency-8-4", "\ud83e\udee0\ud83e\udee0\ud83e\udee0a", 10 };
        yield return new object[] { "category-adjacency-8-5", "\ud83e\udee0\u00e9\ud83e\udee0\u4e2d", 8 };
        yield return new object[] { "category-adjacency-9-0", "\u0085abc", 3 };
        yield return new object[] { "category-adjacency-9-1", "hello\u0085World", 4 };
        yield return new object[] { "category-adjacency-9-2", "\u0085 12 \u0085", 6 };
        yield return new object[] { "category-adjacency-9-3", "a\u0085's", 4 };
        yield return new object[] { "category-adjacency-9-4", "\u0085\u0085\u0085a", 7 };
        yield return new object[] { "category-adjacency-9-5", "\u0085\u00e9\u0085\u4e2d", 6 };
        yield return new object[] { "category-adjacency-10-0", "\u2028abc", 2 };
        yield return new object[] { "category-adjacency-10-1", "hello\u2028World", 3 };
        yield return new object[] { "category-adjacency-10-2", "\u2028 12 \u2028", 4 };
        yield return new object[] { "category-adjacency-10-3", "a\u2028's", 3 };
        yield return new object[] { "category-adjacency-10-4", "\u2028\u2028\u2028a", 4 };
        yield return new object[] { "category-adjacency-10-5", "\u2028\u00e9\u2028\u4e2d", 4 };
        yield return new object[] { "category-adjacency-11-0", "\u0301abc", 2 };
        yield return new object[] { "category-adjacency-11-1", "hello\u0301World", 3 };
        yield return new object[] { "category-adjacency-11-2", "\u0301 12 \u0301", 5 };
        yield return new object[] { "category-adjacency-11-3", "a\u0301's", 3 };
        yield return new object[] { "category-adjacency-11-4", "\u0301\u0301\u0301a", 4 };
        yield return new object[] { "category-adjacency-11-5", "\u0301\u00e9\u0301\u4e2d", 4 };
        yield return new object[] { "category-adjacency-12-0", "\u200dabc", 2 };
        yield return new object[] { "category-adjacency-12-1", "hello\u200dWorld", 3 };
        yield return new object[] { "category-adjacency-12-2", "\u200d 12 \u200d", 4 };
        yield return new object[] { "category-adjacency-12-3", "a\u200d's", 4 };
        yield return new object[] { "category-adjacency-12-4", "\u200d\u200d\u200da", 4 };
        yield return new object[] { "category-adjacency-12-5", "\u200d\u00e9\u200d\u4e2d", 4 };
        yield return new object[] { "category-adjacency-13-0", "\ufe0fabc", 2 };
        yield return new object[] { "category-adjacency-13-1", "hello\ufe0fWorld", 3 };
        yield return new object[] { "category-adjacency-13-2", "\ufe0f 12 \ufe0f", 5 };
        yield return new object[] { "category-adjacency-13-3", "a\ufe0f's", 3 };
        yield return new object[] { "category-adjacency-13-4", "\ufe0f\ufe0f\ufe0fa", 4 };
        yield return new object[] { "category-adjacency-13-5", "\ufe0f\u00e9\ufe0f\u4e2d", 4 };
    }

    [Theory]
    [MemberData(nameof(Adjacency))]
    public async Task Scalar_counts_match_frozen_reference_through_sync_and_default_async(string identity, string text, int expected)
    {
        _ = identity;
        Assert.Equal(expected, new StructuredTextTokenCounter().CountText(text));
        var operation = new BoundedStructuredTextTokenCounter().BeginCount(text);
        Assert.Equal(expected, await operation.Caller);
        await operation.Completion;
    }

    public static IEnumerable<object[]> Pieces()
    {
        yield return new object[] { "a\ud801\udc00's", new string[] { "a", "\ud801\udc00's" }, 6 };
        yield return new object[] { "a\ud835\udc9c's", new string[] { "a", "\ud835\udc9c's" }, 5 };
        yield return new object[] { "\ud801\udc00\ud801\udc28", new string[] { "\ud801\udc00\ud801\udc28" }, 8 };
        yield return new object[] { "ABcD", new string[] { "ABc", "D" }, 3 };
        yield return new object[] { "ABC", new string[] { "ABC" }, 1 };
        yield return new object[] { "aABC", new string[] { "a", "ABC" }, 2 };
        yield return new object[] { "a\u0301BC", new string[] { "a\u0301", "BC" }, 3 };
        yield return new object[] { "\u0301", new string[] { "\u0301" }, 1 };
        yield return new object[] { "\u0301A", new string[] { "\u0301", "A" }, 2 };
        yield return new object[] { "\u0301\u0301A", new string[] { "\u0301\u0301", "A" }, 3 };
        yield return new object[] { " A", new string[] { " A" }, 1 };
        yield return new object[] { "  A", new string[] { " ", " A" }, 2 };
        yield return new object[] { "  ", new string[] { "  " }, 1 };
        yield return new object[] { "   x", new string[] { "  ", " x" }, 2 };
        yield return new object[] { "\t\tX", new string[] { "\t", "\tX" }, 2 };
        yield return new object[] { "\t \r\n  x", new string[] { "\t \r\n", " ", " x" }, 3 };
        yield return new object[] { " \n \r   ", new string[] { " \n \r", "   " }, 4 };
        yield return new object[] { "!!\r\n/a", new string[] { "!!\r\n/", "a" }, 4 };
        yield return new object[] { " !!/\r\n a", new string[] { " !!/\r\n", " a" }, 3 };
        yield return new object[] { "1234567", new string[] { "123", "456", "7" }, 3 };
        yield return new object[] { "\ud835\udfce\ud835\udfcf\ud835\udfd0\ud835\udfd1", new string[] { "\ud835\udfce\ud835\udfcf\ud835\udfd0", "\ud835\udfd1" }, 12 };
        yield return new object[] { "WE'RE They've I'll don't", new string[] { "WE'RE", " They've", " I'll", " don't" }, 6 };
        yield return new object[] { "a'\u017f a'LL a'\u212a a'RE", new string[] { "a'\u017f", " a'LL", " a", "'\u212a", " a'RE" }, 13 };
        yield return new object[] { "<|endoftext|>", new string[] { "<|", "endoftext", "|>" }, 7 };
        yield return new object[] { "\u0085 a\u2028B", new string[] { "\u0085", " a", "\u2028B" }, 5 };
    }

    [Theory]
    [MemberData(nameof(Pieces))]
    public void Scalar_pieces_preserve_reference_boundaries_and_exact_utf16_offsets(string text, string[] expected, int count)
    {
        var tokenizer = new O200kOrdinaryPreTokenizer();
        var ranges = tokenizer.PreTokenize(text).ToArray();
        Assert.Equal(ranges, tokenizer.PreTokenize(text.AsSpan()).ToArray());
        Assert.Equal(expected, ranges.Select(range => text.Substring(range.Offset, range.Length)).ToArray());
        var offset = 0;
        foreach (var range in ranges)
        {
            Assert.Equal(offset, range.Offset);
            Assert.True(range.Length > 0);
            offset += range.Length;
        }
        Assert.Equal(text.Length, offset);
        Assert.Equal(count, new StructuredTextTokenCounter().CountText(text));
    }
}
