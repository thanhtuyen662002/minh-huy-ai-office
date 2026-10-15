using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace MinhHuy.AIOffice.Agent.Worker;

// The seven ordered ordinary o200k alternatives, evaluated on Unicode scalars.
// .NET regex categories operate on UTF16 code units and split astral letters
// incorrectly. Returned ranges still refer to the unchanged original UTF16 text.
// Pattern origin: OpenAI tiktoken; pinned Microsoft tokenizer notices apply.
internal sealed class O200kOrdinaryPreTokenizer : PreTokenizer
{
    public override IEnumerable<(int Offset, int Length)> PreTokenize(string text) => Split(text.AsSpan());
    public override IEnumerable<(int Offset, int Length)> PreTokenize(ReadOnlySpan<char> text) => Split(text);

    private static List<(int Offset, int Length)> Split(ReadOnlySpan<char> text)
    {
        var scalars = new List<Scalar>();
        for (var offset = 0; offset < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text[offset..], out var rune, out var length) != OperationStatus.Done)
                throw new InvalidOperationException(StructuredTextTokenCounter.UnavailableMessage);
            scalars.Add(new Scalar(rune, offset));
            offset += length;
        }

        var result = new List<(int Offset, int Length)>();
        for (var start = 0; start < scalars.Count;)
        {
            var end = MatchLetters(scalars, start, lowerRequired: true);
            if (end < 0) end = MatchLetters(scalars, start, lowerRequired: false);
            if (end < 0 && scalars[start].Number)
            {
                end = start + 1;
                while (end < scalars.Count && end - start < 3 && scalars[end].Number) end++;
            }
            if (end < 0)
            {
                var body = scalars[start].Value == ' ' ? start + 1 : start;
                end = MatchPunctuation(scalars, body);
                if (end < 0 && body != start) end = MatchPunctuation(scalars, start);
            }
            if (end < 0)
            {
                var whiteEnd = start;
                var lastNewline = -1;
                while (whiteEnd < scalars.Count && scalars[whiteEnd].Whitespace)
                {
                    if (scalars[whiteEnd].Newline) lastNewline = whiteEnd;
                    whiteEnd++;
                }
                // \s*[\r\n]+ consumes through the last newline, leaving trailing space.
                if (lastNewline >= start) end = lastNewline + 1;
                // \s+(?!\S) backtracks one scalar before following nonwhite text.
                else if (whiteEnd == scalars.Count) end = whiteEnd;
                else if (whiteEnd - start > 1) end = whiteEnd - 1;
                else if (whiteEnd > start) end = whiteEnd;
            }
            if (end <= start) throw new InvalidOperationException(StructuredTextTokenCounter.UnavailableMessage);
            var beginOffset = scalars[start].Offset;
            var endOffset = end == scalars.Count ? text.Length : scalars[end].Offset;
            result.Add((beginOffset, endOffset - beginOffset));
            start = end;
        }
        return result;
    }

    private static int MatchLetters(List<Scalar> text, int start, bool lowerRequired)
    {
        var prefix = text[start];
        if (!prefix.Newline && !prefix.Letter && !prefix.Number)
        {
            var prefixedEnd = MatchLetterBody(text, start + 1, lowerRequired);
            if (prefixedEnd >= 0) return MatchContraction(text, prefixedEnd);
        }
        var end = MatchLetterBody(text, start, lowerRequired);
        return end < 0 ? -1 : MatchContraction(text, end);
    }

    private static int MatchLetterBody(List<Scalar> text, int start, bool lowerRequired)
    {
        var end = start;
        while (end < text.Count && text[end].Upper) end++;
        if (!lowerRequired)
        {
            if (end == start) return -1;
            while (end < text.Count && text[end].Lower) end++;
            return end;
        }
        if (end < text.Count && text[end].Lower)
        {
            while (end < text.Count && text[end].Lower) end++;
            return end;
        }
        // Upper/lower classes overlap on marks, modifier and uncased letters.
        // Backtrack the greedy upper* to its last scalar satisfying lower+.
        for (var index = end - 1; index >= start; index--)
            if (text[index].Lower) return index + 1;
        return -1;
    }

    private static int MatchContraction(List<Scalar> text, int start)
    {
        if (start + 1 >= text.Count || text[start].Value != '\'') return start;
        var first = Rune.ToUpperInvariant(text[start + 1].Rune).Value;
        if (first is 'S' or 'T' or 'M' or 'D') return start + 2;
        if (start + 2 < text.Count)
        {
            var second = Rune.ToUpperInvariant(text[start + 2].Rune).Value;
            if ((first == 'R' && second == 'E') || (first == 'V' && second == 'E') || (first == 'L' && second == 'L'))
                return start + 3;
        }
        return start;
    }

    private static int MatchPunctuation(List<Scalar> text, int start)
    {
        var end = start;
        while (end < text.Count && !text[end].Whitespace && !text[end].Letter && !text[end].Number) end++;
        if (end == start) return -1;
        while (end < text.Count && (text[end].Newline || text[end].Value == '/')) end++;
        return end;
    }

    private readonly struct Scalar
    {
        internal Scalar(Rune rune, int offset)
        {
            Rune = rune;
            Offset = offset;
            var category = Rune.GetUnicodeCategory(rune);
            var shared = category is UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            Upper = shared || category is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter;
            Lower = shared || category == UnicodeCategory.LowercaseLetter;
            Letter = Rune.IsLetter(rune);
            Number = category is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
            Whitespace = Rune.IsWhiteSpace(rune);
        }

        internal Rune Rune { get; }
        internal int Value => Rune.Value;
        internal int Offset { get; }
        internal bool Upper { get; }
        internal bool Lower { get; }
        internal bool Letter { get; }
        internal bool Number { get; }
        internal bool Whitespace { get; }
        internal bool Newline => Value is '\r' or '\n';
    }
}
