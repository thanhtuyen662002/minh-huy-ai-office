using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace MinhHuy.AIOffice.Platform.Persistence;

public enum GroupSecretQuarantineReason
{
    NoMatch = 1, InvalidInput = 2, CredentialAssignment = 3, Authorization = 4,
    PrivateKey = 5, SignedToken = 6, ProviderCredential = 7, CredentialUri = 8, SignedUri = 9, XmlCredential = 10
}

// This receipt contains policy metadata only. No match, original text, secret
// hash, position or replacement text can escape into a log/attention record.
public sealed class GroupSecretQuarantineDecision
{
    internal GroupSecretQuarantineDecision(GroupSecretQuarantineReason reason) { Reason = reason; }
    public string PolicyVersion => GroupSecretQuarantine.PolicyVersion;
    public GroupSecretQuarantineReason Reason { get; }
    public bool RequiresQuarantine => Reason != GroupSecretQuarantineReason.NoMatch;
    public override string ToString() => $"Group secret quarantine decision ({PolicyVersion}, {Reason}).";
}

// A conservative, bounded known-form detector. NoMatch is not proof that
// arbitrary text is secret-free. Consumers must apply this policy both to
// contributing input fields and decoded output fields before any release.
public static partial class GroupSecretQuarantine
{
    public const string PolicyVersion = "group-secret-quarantine-v1";
    public const int MaximumTextLength = 65536;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static GroupSecretQuarantineDecision Inspect(string? text)
    {
        if (text is null || text.Length > MaximumTextLength) return Decision(GroupSecretQuarantineReason.InvalidInput);
        // Reject malformed UTF16 before normalization. The inspection view
        // never replaces original source bytes, identities, hashes or quotes.
        if (!WellFormed(text)) return Decision(GroupSecretQuarantineReason.InvalidInput);
        var normalized = text.Normalize(NormalizationForm.FormKC);
        if (normalized.Length > MaximumTextLength) return Decision(GroupSecretQuarantineReason.InvalidInput);
        var view = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category != UnicodeCategory.Format && (category != UnicodeCategory.Control || Rune.IsWhiteSpace(rune)))
                view.Append(rune.ToString());
        }
        var inspected = view.ToString();
        try
        {
            if (Assignments().IsMatch(inspected)) return Decision(GroupSecretQuarantineReason.CredentialAssignment);
            if (Authorization().IsMatch(inspected)) return Decision(GroupSecretQuarantineReason.Authorization);
            if (PrivateKeys().IsMatch(inspected)) return Decision(GroupSecretQuarantineReason.PrivateKey);
            if (SignedTokens().IsMatch(inspected)) return Decision(GroupSecretQuarantineReason.SignedToken);
            if (ProviderCredentials().IsMatch(inspected)) return Decision(GroupSecretQuarantineReason.ProviderCredential);
            if (CredentialUris().IsMatch(inspected)) return Decision(GroupSecretQuarantineReason.CredentialUri);
            if (SignedUris().IsMatch(inspected)) return Decision(GroupSecretQuarantineReason.SignedUri);
            if (XmlCredentials().IsMatch(inspected)) return Decision(GroupSecretQuarantineReason.XmlCredential);
            return Decision(GroupSecretQuarantineReason.NoMatch);
        }
        catch (RegexMatchTimeoutException) { return Decision(GroupSecretQuarantineReason.InvalidInput); }
    }

    private static GroupSecretQuarantineDecision Decision(GroupSecretQuarantineReason reason) => new(reason);

    private static bool WellFormed(string text)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    // Call after transport validation and before interpreting/persisting any
    // model fields. Decode JSON strings first: inspecting wire escapes alone
    // would miss a credential key/value assembled by JSON unicode escapes.
    public static GroupSecretQuarantineDecision InspectOutputJson(string? output)
    {
        if (output is null || output.Length > MaximumTextLength || !WellFormed(output))
            return Decision(GroupSecretQuarantineReason.InvalidInput);
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 12 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Decision(GroupSecretQuarantineReason.InvalidInput);
            var inspectedNodes = 0;
            return Visit(document.RootElement);

            GroupSecretQuarantineDecision Visit(JsonElement element)
            {
                if (++inspectedNodes > 1024) return Decision(GroupSecretQuarantineReason.InvalidInput);
                if (element.ValueKind == JsonValueKind.String) return Inspect(element.GetString());
                if (element.ValueKind == JsonValueKind.Object)
                    foreach (var property in element.EnumerateObject())
                    {
                        var key = Inspect(property.Name); if (key.RequiresQuarantine) return key;
                        // A credential assignment is the decoded key AND its
                        // value. Inspecting them independently loses that
                        // relation (including escaped keys and numeric values).
                        // Quotes retain even an empty credential assignment;
                        // null/container assignments also fail conservatively.
                        var assignedValue = property.Value.ValueKind == JsonValueKind.String
                            ? "\"" + property.Value.GetString() + "\"" : property.Value.GetRawText();
                        var assignment = Inspect(property.Name + ":" + assignedValue);
                        if (assignment.RequiresQuarantine) return assignment;
                        var value = Visit(property.Value); if (value.RequiresQuarantine) return value;
                    }
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var item in element.EnumerateArray())
                    {
                        var value = Visit(item); if (value.RequiresQuarantine) return value;
                    }
                return Decision(GroupSecretQuarantineReason.NoMatch);
            }
        }
        catch (JsonException) { return Decision(GroupSecretQuarantineReason.InvalidInput); }
        catch (ArgumentException) { return Decision(GroupSecretQuarantineReason.InvalidInput); }
        catch (InvalidOperationException) { return Decision(GroupSecretQuarantineReason.InvalidInput); }
    }

    [GeneratedRegex("\\b(?:password|pwd|passwd|passphrase|secret|client[_ -]?secret|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|id[_ -]?token|token|connection[_ -]?string|account[_ -]?key|sas[_ -]?token)\\b[\"']?\\s*[:=]\\s*\\S", Options, 100)]
    private static partial Regex Assignments();

    [GeneratedRegex("\\b(?:(?:proxy-)?authorization\\b[\"']?\\s*[:=]\\s*[\"']?\\s*(?:bearer|basic|digest)\\s+\\S|bearer\\s+[A-Za-z0-9_./+~=-]{8,})", Options, 100)]
    private static partial Regex Authorization();

    [GeneratedRegex("-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----", Options, 100)]
    private static partial Regex PrivateKeys();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{5,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b", Options, 100)]
    private static partial Regex SignedTokens();

    [GeneratedRegex("\\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|xox[baprs]-[A-Za-z0-9-]{10,})\\b", Options, 100)]
    private static partial Regex ProviderCredentials();

    [GeneratedRegex("\\b[a-z][a-z0-9+.-]*://[^\\s/:@]+:[^\\s/@]+@", Options, 100)]
    private static partial Regex CredentialUris();

    [GeneratedRegex("[?&](?:sig|signature|x-amz-signature|x-goog-signature|token|api_key|access_token)=[^&#\\s]+", Options, 100)]
    private static partial Regex SignedUris();

    [GeneratedRegex("<(?:password|pwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)>\\s*[^<\\s]", Options, 100)]
    private static partial Regex XmlCredentials();
}
