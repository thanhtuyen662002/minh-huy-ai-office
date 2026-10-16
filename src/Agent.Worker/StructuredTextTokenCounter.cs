using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace MinhHuy.AIOffice.Agent.Worker;

// Text-only tokenization. This neither selects a model nor qualifies provider
// framing/schema overhead, a context window, aliases, billing or permissions.
// No provider connection, configuration loader or HTTP client is added.
internal sealed class StructuredTextTokenCounter
{
    internal const string Version = "ml-tokenizers-2.0.0-o200k-base-ordinary-scalar-v2";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);
    internal const int MaximumTextBytes = StructuredResponsesPolicy.InputByteLimit;
    internal const string UnavailableMessage = "Structured text tokenization is unavailable.";

    internal int CountText(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (text is null || text.Length > MaximumTextBytes || Utf8.GetByteCount(text) > MaximumTextBytes)
                throw Unavailable();
            // Shared library caches are serialized; user text never becomes a
            // special control token and no Unicode normalization is performed.
            int count;
            lock (Tokenizer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count = Tokenizer.Value.CountTokens(text);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (count < 0 || (text.Length != 0 && count == 0)) throw Unavailable();
            return count;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or IOException or OverflowException)
        {
            // No input, tokenizer exception, key, endpoint or inner error escapes.
            throw Unavailable();
        }
    }

    private static TiktokenTokenizer Create()
    {
        // Use only the version-pinned package's local data, never a runtime URL
        // or user-selected vocabulary. Preserve ordinary special-token-looking
        // text: the library's CreateForEncoding factory adds built-in specials.
        using var compressed = Assembly.Load("Microsoft.ML.Tokenizers.Data.O200kBase")
            .GetManifestResourceStream("o200k_base.tiktoken.deflate") ?? throw Unavailable();
        using var vocabulary = new DeflateStream(compressed, CompressionMode.Decompress);
        return TiktokenTokenizer.Create(vocabulary, new O200kOrdinaryPreTokenizer(),
            normalizer: null, specialTokens: null, cacheSize: 4096);
    }

    public override string ToString() => "Structured text tokenizer (local, no model qualification).";
    private static InvalidOperationException Unavailable() => new(UnavailableMessage);
}
