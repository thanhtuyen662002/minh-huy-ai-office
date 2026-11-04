using System.Text.RegularExpressions;

namespace MinhHuy.AIOffice.Shared.Contracts;

/// <summary>
/// Pictures attached to a Trợ lý question travel inside the question text as "[img:&lt;id&gt;]" tokens, so the
/// existing submission, idempotency and fingerprint rules cover them unchanged. The picture bytes live in
/// the AssistantImages table; the worker resolves the tokens for the task's own asker only.
/// </summary>
public static partial class AssistantImageTokens
{
    public const int MaximumImages = 6;

    [GeneratedRegex(@"\[img:([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\]", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    /// <summary>The question without tokens, and the distinct picture ids in order (at most <see cref="MaximumImages"/>).</summary>
    public static (string Text, IReadOnlyList<Guid> ImageIds) Parse(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var ids = new List<Guid>();
        foreach (Match match in Token().Matches(question))
        {
            var id = Guid.Parse(match.Groups[1].Value);
            if (!ids.Contains(id) && ids.Count < MaximumImages) ids.Add(id);
        }
        var text = Regex.Replace(Token().Replace(question, " "), @"\s{2,}", " ").Trim();
        return (text, ids);
    }

    public static string Format(Guid id) => $"[img:{id:D}]";
}
