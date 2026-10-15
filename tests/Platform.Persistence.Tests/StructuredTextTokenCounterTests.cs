extern alias RuntimeWorker;
using System.Text;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Real offline BPE data/library, no model/provider calls or model eval claim.
public sealed class StructuredTextTokenCounterTests
{
    private readonly StructuredTextTokenCounter counter = new();

    [Fact]
    public void Published_ordinary_example_has_sixteen_tokens_not_its_byte_count()
    {
        const string text = "Text tokenization is the process of splitting a string into a list of tokens.";
        Assert.Equal(16, counter.CountText(text));
        Assert.NotEqual(Encoding.UTF8.GetByteCount(text), counter.CountText(text));
        Assert.Equal(0, counter.CountText(""));
    }

    [Theory]
    [InlineData("<|endoftext|>")]
    [InlineData("<|endofprompt|>")]
    public void Customer_special_token_looking_strings_are_ordinary_text(string text)
    {
        Assert.True(counter.CountText(text) > 1);
        Assert.True(counter.CountText("a" + text + "b") > 1);
    }

    [Theory]
    [InlineData("Tra cứu tồn kho, nhập xuất")]
    [InlineData("👩🏽‍💻📦")]
    [InlineData("库存\r\n𐐀𐐨")]
    [InlineData("e\u0301 é")]
    public void Valid_unicode_and_raw_whitespace_are_not_changed(string text)
    {
        var first = counter.CountText(text);
        Assert.True(first > 0);
        Assert.Equal(first, counter.CountText(text));
    }

    [Fact]
    public void Limits_invalid_unicode_and_null_refuse_without_releasing_input()
    {
        foreach (var text in new[] { new string('x', 128001), new string('é', 64001), "PRIVATE_REJECTED_CANARY\ud800", null })
        {
            var error = Assert.Throws<InvalidOperationException>(() => counter.CountText(text!));
            Assert.Equal(StructuredTextTokenCounter.UnavailableMessage, error.Message);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("PRIVATE_REJECTED_CANARY", error.ToString());
        }
        Assert.True(counter.CountText(new string('é', 64000)) > 0);
    }

    [Fact]
    public void Original_caller_cancellation_is_preserved()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => counter.CountText("text", source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
    }
}
