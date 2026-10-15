using System.Security.Cryptography;
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
    // Pure common fingerprint construction, not release/catalog authority.
    // Keeping it here lets the host compare planning to actual measured
    // snapshots without duplicating a provider/configuration or hash format.
    public static AiStructuredPlanningMetadata Create(string gatewaySha256, string schemaSha256, string tokenizerVersion,
        string primaryModel, int maximumInputTextTokens, int schemaTokens, IReadOnlyList<string> candidateHashes)
    {
        if (schemaTokens <= 0 || candidateHashes is null || candidateHashes.Count is < 1 or > 2
            || candidateHashes.Any(h => h is not { Length: 64 } || h.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))) throw Invalid();
        // Validate scalar/UTF8 fields before writing any supplied value.
        _ = new AiStructuredPlanningMetadata(gatewaySha256, schemaSha256, new('0', 64), tokenizerVersion, primaryModel, maximumInputTextTokens);
        using var stream = new MemoryStream();
        try
        {
            using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
            writer.Write("ai-structured-planning-v1"); writer.Write(gatewaySha256); writer.Write(schemaSha256);
            writer.Write(tokenizerVersion); writer.Write(maximumInputTextTokens); writer.Write(schemaTokens); writer.Write(candidateHashes.Count);
            foreach (var hash in candidateHashes) writer.Write(hash);
            writer.Flush(); var fingerprint = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
            return new(gatewaySha256, schemaSha256, fingerprint, tokenizerVersion, primaryModel, maximumInputTextTokens);
        }
        finally { CryptographicOperations.ZeroMemory(stream.GetBuffer()); }
    }
    public bool MatchesMeasurement(AiStructuredAdmissionSnapshot measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var maximum = measurement.Routes.Min(route => checked(route.ContextWindowTokens - route.MaxOutputTokens - route.FramingReserveTokens - measurement.SchemaTokens));
        if (maximum <= 0) return false;
        var actual = Create(measurement.GatewaySha256, measurement.SchemaSha256, measurement.TokenizerVersion, measurement.Routes[0].ConfiguredModel,
            (int)Math.Min(maximum, int.MaxValue), measurement.SchemaTokens, measurement.Routes.Select(x => x.CandidateSha256).ToArray());
        return GatewaySha256 == actual.GatewaySha256 && SchemaSha256 == actual.SchemaSha256 && ProfileSetSha256 == actual.ProfileSetSha256
            && TokenizerVersion == actual.TokenizerVersion && ConfiguredPrimaryModel == actual.ConfiguredPrimaryModel && MaximumInputTextTokens == actual.MaximumInputTextTokens;
    }
    public override string ToString() => "AI structured planning (private metadata, no release authority).";
    private static void Canonical(string value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > limit || value.Any(char.IsControl)
            || new UTF8Encoding(false, true).GetByteCount(value) > limit) throw Invalid();
    }
    private static ArgumentException Invalid() => new("Structured planning metadata is invalid.");
}
