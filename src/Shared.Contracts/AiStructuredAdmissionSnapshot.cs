using System.Security.Cryptography;
using System.Text;

namespace MinhHuyAiOffice.Shared.Contracts;

// Historical measurement metadata only. Constructing or persisting this object
// grants no trust, prepared handle, provider invocation or tenant/SQL authority.
public sealed class AiStructuredAdmissionSnapshot
{
    public const string FormatVersion = "ai-structured-admission-snapshot-v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public AiStructuredAdmissionSnapshot(string gatewaySha256, long wholeOperationTimeoutTicks,
        string inputSha256, int inputBytes, string schemaSha256, int schemaBytes,
        string tokenizerVersion, int inputTokens, int schemaTokens, IReadOnlyList<AiStructuredAdmissionRoute> routes)
    {
        try
        {
            RequireHash(gatewaySha256); RequireHash(inputSha256); RequireHash(schemaSha256);
            RequireCanonical(tokenizerVersion, 128);
            if (wholeOperationTimeoutTicks <= 0 || wholeOperationTimeoutTicks > TimeSpan.FromSeconds(60).Ticks
                || inputBytes is < 1 or > 128000 || schemaBytes is < 1 or > 32000
                || inputTokens <= 0 || schemaTokens <= 0 || routes is null || routes.Count is < 1 or > 2) throw Invalid();
            var copied = routes.ToArray();
            if (copied.Any(x => x is null) || copied.Where((route, index) => route.Ordinal != index).Any()
                || copied.Select(x => x.ProviderId).Distinct(StringComparer.Ordinal).Count() != copied.Length) throw Invalid();
            foreach (var route in copied)
            {
                if (route.MaximumInputTokens != checked((long)inputTokens + schemaTokens + route.FramingReserveTokens)
                    || checked(route.MaximumInputTokens + route.MaxOutputTokens) > route.ContextWindowTokens) throw Invalid();
            }
            GatewaySha256 = gatewaySha256; WholeOperationTimeoutTicks = wholeOperationTimeoutTicks;
            InputSha256 = inputSha256; InputBytes = inputBytes; SchemaSha256 = schemaSha256; SchemaBytes = schemaBytes;
            TokenizerVersion = tokenizerVersion; InputTokens = inputTokens; SchemaTokens = schemaTokens;
            Routes = Array.AsReadOnly(copied);
            using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer, Utf8, true);
            writer.Write(FormatVersion); writer.Write(GatewaySha256); writer.Write(WholeOperationTimeoutTicks);
            writer.Write(InputSha256); writer.Write(InputBytes); writer.Write(SchemaSha256); writer.Write(SchemaBytes);
            writer.Write(TokenizerVersion); writer.Write(InputTokens); writer.Write(SchemaTokens); writer.Write(Routes.Count);
            foreach (var route in Routes)
            {
                writer.Write(route.Ordinal); writer.Write(route.ProviderId); writer.Write(route.ConfiguredModel);
                writer.Write(route.EndpointSha256); writer.Write(route.CandidateSha256); writer.Write(route.EvidenceSha256);
                writer.Write(route.ContextWindowTokens); writer.Write(route.FramingReserveTokens);
                writer.Write(route.MaxOutputTokens); writer.Write(route.MaximumInputTokens); writer.Write(route.EchoedModels.Count);
                foreach (var model in route.EchoedModels) writer.Write(model);
            }
            writer.Flush();
            SnapshotSha256 = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException) { throw Invalid(); }
    }

    public string GatewaySha256 { get; }
    public long WholeOperationTimeoutTicks { get; }
    public string InputSha256 { get; }
    public int InputBytes { get; }
    public string SchemaSha256 { get; }
    public int SchemaBytes { get; }
    public string TokenizerVersion { get; }
    public int InputTokens { get; }
    public int SchemaTokens { get; }
    public IReadOnlyList<AiStructuredAdmissionRoute> Routes { get; }
    public string SnapshotSha256 { get; }
    public override string ToString() => "AI structured admission snapshot (non-authorizing metadata).";

    internal static void RequireHash(string? value)
    {
        if (value is not { Length: 64 } || value.Any(x => x is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) throw Invalid();
    }
    internal static void RequireCanonical(string? value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumBytes || value != value.Trim()
            || value.Any(char.IsControl) || Utf8.GetByteCount(value) > maximumBytes) throw Invalid();
    }
    internal static ArgumentException Invalid() => new("AI structured admission snapshot metadata is invalid.");
}

public sealed class AiStructuredAdmissionRoute
{
    public AiStructuredAdmissionRoute(int ordinal, string providerId, string configuredModel,
        string endpointSha256, string candidateSha256, string evidenceSha256, long contextWindowTokens,
        int framingReserveTokens, int maxOutputTokens, long maximumInputTokens, IReadOnlyList<string> echoedModels)
    {
        try
        {
            AiStructuredAdmissionSnapshot.RequireCanonical(providerId, 128);
            AiStructuredAdmissionSnapshot.RequireCanonical(configuredModel, 256);
            AiStructuredAdmissionSnapshot.RequireHash(endpointSha256); AiStructuredAdmissionSnapshot.RequireHash(candidateSha256);
            AiStructuredAdmissionSnapshot.RequireHash(evidenceSha256);
            if (ordinal is < 0 or > 1 || contextWindowTokens <= 0 || framingReserveTokens < 0
                || maxOutputTokens is < 1 or > 32768 || maximumInputTokens <= framingReserveTokens
                || checked(maximumInputTokens + maxOutputTokens) > contextWindowTokens
                || echoedModels is null || echoedModels.Count is < 1 or > 16) throw AiStructuredAdmissionSnapshot.Invalid();
            var copied = echoedModels.ToArray();
            foreach (var model in copied) AiStructuredAdmissionSnapshot.RequireCanonical(model, 256);
            if (copied.Distinct(StringComparer.Ordinal).Count() != copied.Length) throw AiStructuredAdmissionSnapshot.Invalid();
            Ordinal = ordinal; ProviderId = providerId; ConfiguredModel = configuredModel;
            EndpointSha256 = endpointSha256; CandidateSha256 = candidateSha256; EvidenceSha256 = evidenceSha256;
            ContextWindowTokens = contextWindowTokens; FramingReserveTokens = framingReserveTokens;
            MaxOutputTokens = maxOutputTokens; MaximumInputTokens = maximumInputTokens;
            EchoedModels = Array.AsReadOnly(copied.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        catch (Exception error) when (error is ArgumentException or OverflowException) { throw AiStructuredAdmissionSnapshot.Invalid(); }
    }
    public int Ordinal { get; }
    public string ProviderId { get; }
    public string ConfiguredModel { get; }
    public string EndpointSha256 { get; }
    public string CandidateSha256 { get; }
    public string EvidenceSha256 { get; }
    public long ContextWindowTokens { get; }
    public int FramingReserveTokens { get; }
    public int MaxOutputTokens { get; }
    public long MaximumInputTokens { get; }
    public IReadOnlyList<string> EchoedModels { get; }
    public override string ToString() => "AI structured admission route (non-authorizing metadata).";
}
