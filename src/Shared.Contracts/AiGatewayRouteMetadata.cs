using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinhHuyAiOffice.Shared.Contracts;

// Optional configuration metadata from the actual host-owned adapters. These
// descriptors grant no permissions and do not qualify a tokenizer, model,
// context window, alias or production group consumer.
public interface IAiStructuredProviderMetadataSource
{
    AiStructuredRouteMetadata DescribeStructuredRoute();
}

public interface IAiStructuredGatewayMetadataSource
{
    AiStructuredGatewayMetadata DescribeStructuredGateway();
}

public sealed record AiStructuredTransportLimits(string Version, int InputBytes, int SchemaBytes,
    int OutputBytes, int EntityBytes, int JsonDepth, int SchemaDepth, int OperationsPerAdapter)
{
    public override string ToString() => "AI structured transport limits (metadata).";
}

public sealed class AiStructuredRouteMetadata
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public AiStructuredRouteMetadata(string providerId, string configuredModel, string endpointIdentitySha256,
        int maxOutputTokens, TimeSpan perRouteTimeout, AiStructuredTransportLimits limits)
    {
        try
        {
            Canonical(providerId, 128); Canonical(configuredModel, 256);
            if (endpointIdentitySha256 is null || endpointIdentitySha256.Length != 64
                || endpointIdentitySha256.Any(x => x is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
                || maxOutputTokens is < 1 or > 32768 || perRouteTimeout <= TimeSpan.Zero
                || perRouteTimeout > TimeSpan.FromSeconds(60) || limits is null) throw Invalid();
            Canonical(limits.Version, 128);
            if (limits.InputBytes <= 0 || limits.SchemaBytes <= 0 || limits.OutputBytes <= 0
                || limits.EntityBytes < Math.Max(limits.InputBytes, Math.Max(limits.SchemaBytes, limits.OutputBytes))
                || limits.JsonDepth is < 1 or > 128 || limits.SchemaDepth is < 1 or > 128
                || limits.OperationsPerAdapter is < 1 or > 1024) throw Invalid();
        }
        catch (Exception error) when (error is EncoderFallbackException or ArgumentException)
        { throw Invalid(); }
        ProviderId = providerId; ConfiguredModel = configuredModel;
        EndpointIdentitySha256 = endpointIdentitySha256; MaxOutputTokens = maxOutputTokens;
        PerRouteTimeoutTicks = perRouteTimeout.Ticks; Limits = limits;
    }
    public string ProviderId { get; }
    // This is the configured request model, not a qualified echoed model alias.
    public string ConfiguredModel { get; }
    public string EndpointIdentitySha256 { get; }
    public int MaxOutputTokens { get; }
    public long PerRouteTimeoutTicks { get; }
    public AiCapability Capability => AiCapability.StructuredGeneration;
    public AiStructuredTransportLimits Limits { get; }
    public override string ToString() => "AI structured route (private configuration metadata).";
    private static void Canonical(string value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value != value.Trim()
            || value.Any(char.IsControl)) throw Invalid();
        Utf8.GetByteCount(value);
    }
    private static ArgumentException Invalid() => new("AI structured route metadata is invalid.");
}

public sealed class AiStructuredGatewayMetadata
{
    public const string FormatVersion = "ai-structured-gateway-metadata-v1";
    public const string DeadlineFormatVersion = "ai-structured-gateway-metadata-v2";
    public const string FallbackPolicy = "transient-primary-failure-only";
    private AiStructuredGatewayMetadata(AiStructuredRouteMetadata primary, AiStructuredRouteMetadata? backup, TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (backup is not null && primary.ProviderId == backup.ProviderId)
            throw new ArgumentException("AI structured route identities must differ.");
        Routes = Array.AsReadOnly(backup is null ? new[] { primary } : new[] { primary, backup });
        if (deadline is not null && (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(60)))
            throw new ArgumentException("AI structured Gateway deadline metadata is invalid.");
        WholeOperationTimeoutTicks = deadline?.Ticks;
        // Closed metadata only: no options object, credentials, endpoint URI,
        // prompt, tenant/company or observed response is serialized here.
        var encoded = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = FormatVersion,
            fallback = FallbackPolicy,
            routes = Routes,
            accounting = "unavailable",
            whole_operation_deadline = false,
            maximum_physical_invocations = Routes.Count
        });
        MetadataSha256 = Convert.ToHexString(SHA256.HashData(encoded));
        if (deadline is not null)
        {
            var bounded = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = DeadlineFormatVersion,
                fallback = FallbackPolicy,
                routes = Routes,
                accounting = "unavailable",
                whole_operation_deadline = true,
                whole_operation_timeout_ticks = deadline.Value.Ticks,
                ownership = "per-adapter-physical-slots-through-cleanup",
                maximum_physical_invocations = Routes.Count
            });
            MetadataSha256 = Convert.ToHexString(SHA256.HashData(bounded));
        }
    }
    public static AiStructuredGatewayMetadata Create(AiStructuredRouteMetadata primary, AiStructuredRouteMetadata? backup = null) => new(primary, backup);
    public static AiStructuredGatewayMetadata CreateWithDeadline(AiStructuredRouteMetadata primary,
        AiStructuredRouteMetadata? backup, TimeSpan deadline) => new(primary, backup, deadline);
    // Immutable ordered snapshot: primary first, optional backup second.
    public IReadOnlyList<AiStructuredRouteMetadata> Routes { get; }
    public string MetadataSha256 { get; }
    public bool ModelAccountingUnavailable => true;
    public bool ProvidesWholeOperationDeadline => WholeOperationTimeoutTicks is not null;
    public long? WholeOperationTimeoutTicks { get; }
    public int MaximumPhysicalInvocations => Routes.Count;
    public override string ToString() => "AI structured Gateway (private configuration metadata, unqualified).";
}
