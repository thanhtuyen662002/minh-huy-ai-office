using System.Buffers;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Lookup hints from model-eligible current sources only. These values do not
// resolve requests, choose history, link notes or grant SQL/provider authority.
// A consumer must recheck the original source and scope its metadata lookup.
internal sealed class GroupAutomaticHistoryHints
{
    internal const int MaximumRequestCodes = 100;
    private GroupAutomaticHistoryHints(GroupScope scope, string[] codes, string[] replies, bool overflow)
    {
        Scope = scope; RequestCodes = Array.AsReadOnly(codes);
        ReplyMessageIndexes = Array.AsReadOnly(replies); HasAdditionalRequestCodes = overflow;
    }
    internal GroupScope Scope { get; }
    internal IReadOnlyList<string> RequestCodes { get; }
    internal IReadOnlyList<string> ReplyMessageIndexes { get; }
    // A later selector must disclose this limit; it cannot claim complete
    // relevant history or automatically merge requests from a truncated set.
    internal bool HasAdditionalRequestCodes { get; }
    public override string ToString() => "Group history lookup hints (private metadata).";

    internal static GroupAutomaticHistoryHints Create(GroupBatchSourcePreparation preparation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparation); token.ThrowIfCancellationRequested();
        preparation.Scope.Validate();
        if (preparation.Context.Items.Count is < 1 or > 100)
            throw new InvalidOperationException("Group history lookup hints are not available.");
        var eligible = preparation.Candidates.Select(x => x.MessageId).ToHashSet();
        var codes = new List<string>(); var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var replies = new List<string>(); var seenReplies = new HashSet<string>(StringComparer.Ordinal);
        var overflow = false;
        foreach (var source in preparation.Context.Items.OrderBy(x => x.CommittedSequence).ThenBy(x => x.MessageId))
        {
            token.ThrowIfCancellationRequested();
            if (!eligible.Contains(source.MessageId)) continue;
            var text = source.Text!;
            for (var index = 0; index <= text.Length - 36; index++)
            {
                if ((index & 63) == 0) token.ThrowIfCancellationRequested();
                if (index > 0 && IsIdentifierBefore(text, index)
                    || !text.AsSpan(index, 4).Equals("REQ-", StringComparison.OrdinalIgnoreCase)
                    || index + 36 < text.Length && IsIdentifierAfter(text, index + 36)) continue;
                var digits = text.AsSpan(index + 4, 32);
                if (!Guid.TryParseExact(digits, "N", out var id) || id == Guid.Empty) continue;
                var code = "REQ-" + id.ToString("N").ToUpperInvariant();
                if (seenCodes.Contains(code)) continue;
                if (codes.Count == MaximumRequestCodes) { overflow = true; continue; }
                seenCodes.Add(code); codes.Add(code);
            }
            if (source.ReplyToMessageId is { } external)
            {
                var value = GroupIngressIdentity.MessageIndex(preparation.Scope, external);
                if (seenReplies.Add(value)) replies.Add(value);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(preparation.Scope, codes.ToArray(), replies.ToArray(), overflow);
    }

    // Match complete Unicode scalars on either side. Half of a surrogate pair
    // cannot turn an embedded code into a standalone reference. Invalid UTF16
    // boundaries refuse; supplementary symbols remain valid separators.
    private static bool IsIdentifierBefore(string text, int index) =>
        Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out var value, out _) != OperationStatus.Done || IsIdentifier(value);
    private static bool IsIdentifierAfter(string text, int index) =>
        Rune.DecodeFromUtf16(text.AsSpan(index), out var value, out _) != OperationStatus.Done || IsIdentifier(value);
    private static bool IsIdentifier(Rune value) => Rune.IsLetterOrDigit(value) || value.Value is '_' or '-';
}
