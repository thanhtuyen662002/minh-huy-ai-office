using System.Text;

namespace MinhHuyAiOffice.Shared.Contracts;

// Optional metadata from the EXISTING admission owner. This is not a prepared
// request, release/SQL capability, provider client or configuration namespace.
public interface IAiStructuredRequestPlanner
{
    Task<AiStructuredPlanningMetadata> DescribePlanningAsync(string responseSchema, CancellationToken cancellationToken = default);
}

public sealed class AiStructuredPlanningMetadata
{
    public AiStructuredPlanningMetadata(string gatewaySha256, string schemaSha256, string profileSetSha256,
        string tokenizerVersion, string configuredPrimaryModel, int maximumInputTextTokens)
    {
        try
        {
            foreach (var hash in new[] { gatewaySha256, schemaSha256, profileSetSha256 })
                if (hash is not { Length: 64 } || hash.Any(x => x is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) throw Invalid();
            Canonical(tokenizerVersion, 128); Canonical(configuredPrimaryModel, 256);
            if (maximumInputTextTokens <= 0) throw Invalid();
        }
        catch (Exception error) when (error is ArgumentException or EncoderFallbackException) { throw Invalid(); }
        GatewaySha256 = gatewaySha256; SchemaSha256 = schemaSha256; ProfileSetSha256 = profileSetSha256;
        TokenizerVersion = tokenizerVersion; ConfiguredPrimaryModel = configuredPrimaryModel; MaximumInputTextTokens = maximumInputTextTokens;
    }
    public string GatewaySha256 { get; }
    public string SchemaSha256 { get; }
    public string ProfileSetSha256 { get; }
    public string TokenizerVersion { get; }
    public string ConfiguredPrimaryModel { get; }
    // Capacity after EACH released route's output, framing and complete schema
    // text tokens. Complete actual input must still pass real PrepareAsync.
    public int MaximumInputTextTokens { get; }
    public override string ToString() => "AI structured planning (private metadata, no release authority).";
    private static void Canonical(string value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > limit || value.Any(char.IsControl)
            || new UTF8Encoding(false, true).GetByteCount(value) > limit) throw Invalid();
    }
    private static ArgumentException Invalid() => new("Structured planning metadata is invalid.");
}
