using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

// Runtime preparation seam, not a new provider or configuration loader.
// The production constructor uses the released catalog, currently EMPTY.
// Synthetic catalog construction is internal and never registered in host DI.
internal sealed class StructuredModelAdmission : IAiStructuredRequestAdmission
{
    internal const string UnavailableMessage = "Structured model admission is not qualified.";
    private readonly OrderedFailoverAiGateway gateway;
    private readonly StructuredModelProfileCatalog catalog;
    private readonly BoundedStructuredTextTokenCounter counter;
    private readonly TimeProvider clock;
    internal static readonly TimeSpan MaximumPreparationLifetime = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan PreparationBudget = TimeSpan.FromSeconds(30);
    private readonly object issuer = new();

    internal StructuredModelAdmission(OrderedFailoverAiGateway gateway)
        : this(gateway, StructuredModelProfileCatalog.Released, new BoundedStructuredTextTokenCounter()) { }

    internal StructuredModelAdmission(OrderedFailoverAiGateway gateway, StructuredModelProfileCatalog catalog,
        BoundedStructuredTextTokenCounter counter, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(gateway); ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(counter);
        this.gateway = gateway; this.catalog = catalog; this.counter = counter;
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<IAiPreparedStructuredRequest> PrepareAsync(AiGatewayRequest request, CancellationToken caller = default)
    {
        caller.ThrowIfCancellationRequested();
        var began = clock.GetTimestamp();
        using var timeout = new CancellationTokenSource(PreparationBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timeout.Token);
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Input is null || request.Input.Length > StructuredResponsesPolicy.InputByteLimit
                || request.ResponseSchema is null || request.ResponseSchema.Length > StructuredResponsesPolicy.SchemaByteLimit) throw Unavailable();
            request.Validate();
            if (request.Capability != AiCapability.StructuredGeneration) throw Unavailable();
            var input = StructuredResponsesPolicy.BoundedText(request.Input, StructuredResponsesPolicy.InputByteLimit);
            CryptographicOperations.ZeroMemory(input);
            var schema = StructuredResponsesPolicy.BoundedText(request.ResponseSchema!, StructuredResponsesPolicy.SchemaByteLimit);
            string schemaHash;
            try
            {
                using var parsed = StructuredResponsesPolicy.Parse(schema);
                StructuredResponsesPolicy.ValidateSchema(parsed.RootElement);
                schemaHash = Convert.ToHexString(SHA256.HashData(schema));
            }
            finally { CryptographicOperations.ZeroMemory(schema); }
            var metadata = gateway.DescribeStructuredGateway();
            var profiles = catalog.Resolve(metadata, schemaHash);
            // Count complete original strings; BPE is not additive over source
            // fragments. These are text counts, not provider framing evidence.
            var inputTokens = await counter.CountTextAsync(request.Input, linked.Token).ConfigureAwait(false);
            var schemaTokens = await counter.CountTextAsync(request.ResponseSchema!, linked.Token).ConfigureAwait(false);
            RequirePreparationRemaining();
            if (inputTokens <= 0 || schemaTokens <= 0) throw Unavailable();
            var budgets = profiles.Select(profile =>
            {
                var maximumInput = checked((long)inputTokens + schemaTokens + profile.FramingReserveTokens);
                if (checked(maximumInput + profile.MaxOutputTokens) > profile.ContextWindowTokens) throw Unavailable();
                return new RouteBudget(profile, maximumInput);
            }).ToArray();
            catalog.RequireCurrent(metadata, profiles);
            if (gateway.DescribeStructuredGateway().MetadataSha256 != metadata.MetadataSha256) throw Unavailable();
            var prepared = new Prepared(issuer, request, metadata.MetadataSha256, Array.AsReadOnly(budgets), began);
            RequirePreparationRemaining();
            return prepared;

            void RequirePreparationRemaining()
            {
                // Metadata/current-profile checks also belong to the original
                // preparation budget. The two-minute handle lifetime is separate.
                var elapsed = clock.GetElapsedTime(began);
                caller.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested || elapsed < TimeSpan.Zero || elapsed >= PreparationBudget) throw Unavailable();
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Unavailable(); }
    }

    public async Task<AiGatewayResponse> ExecutePreparedAsync(IAiPreparedStructuredRequest handle, CancellationToken caller = default)
    {
        caller.ThrowIfCancellationRequested();
        try
        {
            if (handle is not Prepared prepared) throw Unavailable();
            // A prepared object is issuer-bound and one-use even after provider
            // failure or a lost reply. Retrying requires a new durable attempt.
            prepared.Consume(issuer);
            RequireCurrent();
            using var lifetime = new CancellationTokenSource(Remaining(prepared.Started), clock);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime.Token);
            var response = await gateway.ExecuteAsync(prepared.Request, deadline.Token).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            RequireCurrent();
            response.ValidateFor(prepared.Request);
            var route = prepared.Budgets.SingleOrDefault(x => x.Profile.ProviderId == response.ProviderId) ?? throw Unavailable();
            if (!route.Profile.EchoedModels.Contains(response.Model, StringComparer.Ordinal)
                || response.InputTokens > route.MaximumInputTokens
                || response.OutputTokens > route.Profile.MaxOutputTokens
                || checked(response.InputTokens + response.OutputTokens) > route.Profile.ContextWindowTokens) throw Unavailable();
            // Revalidate release after all post-await metadata/response work.
            _ = Remaining(prepared.Started);
            caller.ThrowIfCancellationRequested();
            if (lifetime.IsCancellationRequested) throw Unavailable();
            return response;

            void RequireCurrent()
            {
                var metadata = gateway.DescribeStructuredGateway();
                _ = Remaining(prepared.Started);
                if (metadata.MetadataSha256 != prepared.MetadataSha256) throw Unavailable();
                catalog.RequireCurrent(metadata, prepared.Budgets.Select(x => x.Profile).ToArray());
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (AiProviderExecutionException error) { throw new AiProviderExecutionException("AI structured provider execution was not confirmed.", error.IsTransient); }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Unavailable(); }
    }

    public override string ToString() => "Structured model admission (host-owned).";
    private static InvalidOperationException Unavailable() => new(UnavailableMessage);
    private TimeSpan Remaining(long began)
    {
        var elapsed = clock.GetElapsedTime(began);
        if (elapsed < TimeSpan.Zero || elapsed >= MaximumPreparationLifetime) throw Unavailable();
        return MaximumPreparationLifetime - elapsed;
    }

    internal sealed record RouteBudget(StructuredModelProfileCandidate Profile, long MaximumInputTokens);

    internal sealed class Prepared(object owner, AiGatewayRequest request, string metadataSha256, IReadOnlyList<RouteBudget> budgets, long began) : IAiPreparedStructuredRequest
    {
        private int consumed;
        internal AiGatewayRequest Request { get; } = request;
        internal string MetadataSha256 { get; } = metadataSha256;
        internal IReadOnlyList<RouteBudget> Budgets { get; } = budgets;
        internal long Started { get; } = began;
        internal void Consume(object currentIssuer)
        {
            if (!ReferenceEquals(owner, currentIssuer) || Interlocked.Exchange(ref consumed, 1) != 0) throw Unavailable();
        }
        public override string ToString() => "Prepared structured request (opaque one-use handle).";
    }
}

// Structural candidate only. A hash/window supplied by a caller is not trust.
// The released catalog is the sole production issuance source and has no entries
// until reviewed provider/model/framing/semantic evidence is actually available.
internal sealed class StructuredModelProfileCandidate
{
    internal StructuredModelProfileCandidate(AiModelProfile registryModel, string gatewaySha256, int routeOrdinal,
        string endpointSha256, string schemaSha256, string tokenizerVersion, long contextWindowTokens,
        int framingReserveTokens, int maxOutputTokens, IReadOnlyList<string> echoedModels, string evidenceSha256)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(registryModel); registryModel.Validate();
            if (!registryModel.IsHealthy || !registryModel.Capabilities.Contains(AiCapability.StructuredGeneration)
                || !Hash(gatewaySha256) || !Hash(endpointSha256) || !Hash(schemaSha256) || !Hash(evidenceSha256)
                || routeOrdinal is < 0 or > 1 || tokenizerVersion != StructuredTextTokenCounter.Version
                || contextWindowTokens <= 0 || framingReserveTokens < 0 || maxOutputTokens is < 1 or > 32768
                || checked((long)framingReserveTokens + maxOutputTokens) >= contextWindowTokens
                || echoedModels is null || echoedModels.Count is < 1 or > 16) throw Invalid();
            if (!StructuredResponsesPolicy.Canonical(registryModel.ProviderId, 128)
                || !StructuredResponsesPolicy.Canonical(registryModel.ModelId, 256)) throw Invalid();
            var models = echoedModels.ToArray();
            if (models.Any(x => !StructuredResponsesPolicy.Canonical(x, 256)) || models.Distinct(StringComparer.Ordinal).Count() != models.Length) throw Invalid();
            ProviderId = registryModel.ProviderId; ConfiguredModel = registryModel.ModelId;
            GatewaySha256 = gatewaySha256; RouteOrdinal = routeOrdinal; EndpointSha256 = endpointSha256;
            SchemaSha256 = schemaSha256; TokenizerVersion = tokenizerVersion; ContextWindowTokens = contextWindowTokens;
            FramingReserveTokens = framingReserveTokens; MaxOutputTokens = maxOutputTokens;
            EchoedModels = Array.AsReadOnly(models.OrderBy(x => x, StringComparer.Ordinal).ToArray()); EvidenceSha256 = evidenceSha256;
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, new UTF8Encoding(false, true), true))
            {
                writer.Write("aioffice-structured-profile-candidate-v1"); writer.Write(ProviderId); writer.Write(ConfiguredModel);
                writer.Write(GatewaySha256); writer.Write(RouteOrdinal); writer.Write(EndpointSha256); writer.Write(SchemaSha256);
                writer.Write(TokenizerVersion); writer.Write(ContextWindowTokens); writer.Write(FramingReserveTokens);
                writer.Write(registryModel.Tier); writer.Write(registryModel.MaxCostPerMillionTokens);
                writer.Write(registryModel.TypicalLatencyMs); writer.Write(registryModel.IsHealthy);
                var capabilities = registryModel.Capabilities.OrderBy(x => x).ToArray();
                writer.Write(capabilities.Length);
                foreach (var capability in capabilities) writer.Write((int)capability);
                writer.Write(MaxOutputTokens); writer.Write(EvidenceSha256); writer.Write(EchoedModels.Count);
                foreach (var model in EchoedModels) writer.Write(model);
            }
            CandidateSha256 = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { throw Invalid(); }
    }
    internal string ProviderId { get; }
    internal string ConfiguredModel { get; }
    internal string GatewaySha256 { get; }
    internal int RouteOrdinal { get; }
    internal string EndpointSha256 { get; }
    internal string SchemaSha256 { get; }
    internal string TokenizerVersion { get; }
    internal long ContextWindowTokens { get; }
    internal int FramingReserveTokens { get; }
    internal int MaxOutputTokens { get; }
    internal IReadOnlyList<string> EchoedModels { get; }
    internal string EvidenceSha256 { get; }
    internal string CandidateSha256 { get; }
    public override string ToString() => "Structured model profile candidate (not release authority).";
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static ArgumentException Invalid() => new("Structured model profile candidate is invalid.");
}

internal sealed class StructuredModelProfileCatalog
{
    // No model/vendor/window is guessed from configuration or tokenizer name.
    internal static StructuredModelProfileCatalog Released { get; } = new([]);
    private readonly IReadOnlyList<StructuredModelProfileCandidate> profiles;
    private readonly HashSet<string> revoked = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private StructuredModelProfileCatalog(IReadOnlyList<StructuredModelProfileCandidate> profiles) => this.profiles = profiles;

    // Offline controls only. Never used by Program or production composition.
    internal static StructuredModelProfileCatalog ForSyntheticControls(params StructuredModelProfileCandidate[] candidates)
    {
        if (candidates is null || candidates.Length is < 1 or > 2 || candidates.Any(x => x is null)
            || candidates.Select(x => x.RouteOrdinal).Distinct().Count() != candidates.Length
            || candidates.Select(x => x.ProviderId).Distinct(StringComparer.Ordinal).Count() != candidates.Length) throw Unavailable();
        return new(Array.AsReadOnly(candidates.ToArray()));
    }

    internal IReadOnlyList<StructuredModelProfileCandidate> Resolve(AiStructuredGatewayMetadata metadata, string schemaHash)
    {
        lock (sync)
        {
            if (metadata is null || !metadata.ProvidesWholeOperationDeadline || metadata.Routes.Count != profiles.Count) throw Unavailable();
            var selected = new List<StructuredModelProfileCandidate>();
            for (var index = 0; index < metadata.Routes.Count; index++)
            {
                var route = metadata.Routes[index];
                var profile = profiles.SingleOrDefault(x => x.RouteOrdinal == index) ?? throw Unavailable();
                if (profile.GatewaySha256 != metadata.MetadataSha256 || profile.ProviderId != route.ProviderId
                    || profile.ConfiguredModel != route.ConfiguredModel || profile.EndpointSha256 != route.EndpointIdentitySha256
                    || profile.MaxOutputTokens != route.MaxOutputTokens || profile.SchemaSha256 != schemaHash
                    || profile.TokenizerVersion != StructuredTextTokenCounter.Version || revoked.Contains(profile.CandidateSha256)) throw Unavailable();
                selected.Add(profile);
            }
            return selected.AsReadOnly();
        }
    }

    internal void RequireCurrent(AiStructuredGatewayMetadata metadata, IReadOnlyList<StructuredModelProfileCandidate> expected)
    {
        if (expected.Count == 0) throw Unavailable();
        var current = Resolve(metadata, expected[0].SchemaSha256);
        if (current.Count != expected.Count || current.Where((profile, index) => !ReferenceEquals(profile, expected[index])).Any()) throw Unavailable();
    }
    internal void RevokeForSyntheticControls(string candidateSha256) { lock (sync) revoked.Add(candidateSha256); }
    public override string ToString() => "Structured model profile catalog (host-owned).";
    private static InvalidOperationException Unavailable() => new(StructuredModelAdmission.UnavailableMessage);
}
