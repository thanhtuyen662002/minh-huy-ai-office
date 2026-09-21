namespace MinhHuyAiOffice.Shared.Contracts;

public enum AiContextSourceKind
{
    Policy,
    UserCompany,
    TaskMemory,
    Conversation,
    RetrievedKnowledge,
    ToolResult
}

public sealed record AiContextSource(
    string SourceId,
    AiContextSourceKind Kind,
    string Content,
    int EstimatedTokens,
    int Priority,
    bool Required = false)
{
    public void Validate()
    {
        Require(SourceId, nameof(SourceId));
        if (string.IsNullOrWhiteSpace(Content)) throw new ArgumentException("Context content is required.", nameof(Content));
        if (EstimatedTokens <= 0) throw new ArgumentException("Estimated tokens must be positive.", nameof(EstimatedTokens));
        if (Priority < 0) throw new ArgumentException("Priority cannot be negative.", nameof(Priority));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be non-empty and canonical.", name);
    }
}

public sealed record AiContextRequest(
    string TenantId,
    string CompanyId,
    string TaskId,
    string ModelId,
    int TokenBudget,
    IReadOnlyList<AiContextSource> Sources)
{
    public void Validate()
    {
        Require(TenantId, nameof(TenantId));
        Require(CompanyId, nameof(CompanyId));
        Require(TaskId, nameof(TaskId));
        Require(ModelId, nameof(ModelId));
        if (TokenBudget <= 0) throw new ArgumentException("Token budget must be positive.", nameof(TokenBudget));
        ArgumentNullException.ThrowIfNull(Sources);
        foreach (var source in Sources) source.Validate();
        if (Sources.GroupBy(x => x.SourceId, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Context source identities must be unique.");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be non-empty and canonical.", name);
    }
}

public sealed record AiContextManifestEntry(string SourceId, AiContextSourceKind Kind, int EstimatedTokens, int Priority);

public sealed record AiContextManifest(
    string TenantId,
    string CompanyId,
    string TaskId,
    string ModelId,
    int TokenBudget,
    int UsedTokens,
    IReadOnlyList<AiContextManifestEntry> Entries);

public sealed record AiContextCheckpoint(
    string CheckpointId,
    AiContextManifest Manifest,
    DateTimeOffset CreatedAtUtc)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CheckpointId) || !string.Equals(CheckpointId, CheckpointId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("CheckpointId must be non-empty and canonical.", nameof(CheckpointId));
        ArgumentNullException.ThrowIfNull(Manifest);
        if (CreatedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Checkpoint timestamp must be UTC.", nameof(CreatedAtUtc));
    }
}

public interface IAiContextCheckpointStore
{
    Task SaveAsync(AiContextCheckpoint checkpoint, CancellationToken cancellationToken = default);
}

public interface IAiContextAssembler
{
    AiContextManifest Assemble(AiContextRequest request);
}

public sealed class DeterministicAiContextAssembler : IAiContextAssembler
{
    public AiContextManifest Assemble(AiContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var required = request.Sources.Where(x => x.Required).ToArray();
        var requiredTokens = required.Sum(x => x.EstimatedTokens);
        if (requiredTokens > request.TokenBudget)
            throw new InvalidOperationException("Required context exceeds the model-specific token budget.");

        var selected = required
            .Concat(request.Sources
                .Where(x => !x.Required)
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.Kind)
                .ThenBy(x => x.SourceId, StringComparer.Ordinal))
            .ToList();

        var entries = new List<AiContextManifestEntry>();
        var used = 0;
        foreach (var source in selected)
        {
            if (used + source.EstimatedTokens > request.TokenBudget) continue;
            used += source.EstimatedTokens;
            entries.Add(new AiContextManifestEntry(source.SourceId, source.Kind, source.EstimatedTokens, source.Priority));
        }

        return new AiContextManifest(request.TenantId, request.CompanyId, request.TaskId, request.ModelId, request.TokenBudget, used, entries);
    }
}

public sealed class DurableAiContextEngine(IAiContextAssembler assembler, IAiContextCheckpointStore checkpointStore)
{
    public async Task<AiContextCheckpoint> AssembleAndCheckpointAsync(
        string checkpointId,
        DateTimeOffset createdAtUtc,
        AiContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var manifest = assembler.Assemble(request);
        var checkpoint = new AiContextCheckpoint(checkpointId, manifest, createdAtUtc);
        checkpoint.Validate();

        // The checkpoint contains only the manifest/reference metadata. Raw source content never crosses this persistence boundary.
        await checkpointStore.SaveAsync(checkpoint, cancellationToken);
        return checkpoint;
    }
}
