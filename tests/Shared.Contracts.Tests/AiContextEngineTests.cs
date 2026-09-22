using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class AiContextEngineTests
{
    [Fact]
    public void Assemble_PreservesScopeAndSelectsWithinBudget()
    {
        var request = new AiContextRequest("tenant-a", "company-a", "task-a", "model-a", 100,
        [
            new("policy", AiContextSourceKind.Policy, "policy", 30, 0, true),
            new("memory", AiContextSourceKind.TaskMemory, "memory", 40, 1),
            new("knowledge", AiContextSourceKind.RetrievedKnowledge, "knowledge", 50, 2)
        ]);

        var manifest = new DeterministicAiContextAssembler().Assemble(request);

        Assert.Equal("tenant-a", manifest.TenantId);
        Assert.Equal("company-a", manifest.CompanyId);
        Assert.Equal("task-a", manifest.TaskId);
        Assert.Equal("model-a", manifest.ModelId);
        Assert.Equal(70, manifest.UsedTokens);
        Assert.Equal(["policy", "memory"], manifest.Entries.Select(x => x.SourceId));
    }

    [Fact]
    public void Assemble_FailsClosedWhenRequiredContextExceedsBudget()
    {
        var request = new AiContextRequest("tenant-a", "company-a", "task-a", "model-a", 20,
        [new("policy", AiContextSourceKind.Policy, "policy", 30, 0, true)]);

        Assert.Throws<InvalidOperationException>(() => new DeterministicAiContextAssembler().Assemble(request));
    }

    [Theory]
    [InlineData(" tenant-a", "company-a", "task-a")]
    [InlineData("tenant-a", "company-a ", "task-a")]
    [InlineData("tenant-a", "company-a", " task-a")]
    public void Assemble_RejectsNonCanonicalAuthorityScope(string tenantId, string companyId, string taskId)
    {
        var request = new AiContextRequest(tenantId, companyId, taskId, "model-a", 100,
        [new("policy", AiContextSourceKind.Policy, "policy", 10, 0, true)]);

        Assert.Throws<ArgumentException>(() => new DeterministicAiContextAssembler().Assemble(request));
    }

    [Fact]
    public void Assemble_RejectsDuplicateSourceIdentity()
    {
        var request = new AiContextRequest("tenant-a", "company-a", "task-a", "model-a", 100,
        [
            new("same", AiContextSourceKind.Policy, "policy", 10, 0, true),
            new("same", AiContextSourceKind.ToolResult, "tool", 10, 1)
        ]);

        Assert.Throws<InvalidOperationException>(() => new DeterministicAiContextAssembler().Assemble(request));
    }

    [Fact]
    public void Manifest_PersistsReferencesAndBudgetMetadata_NotRawPromptContent()
    {
        var request = new AiContextRequest("tenant-a", "company-a", "task-a", "model-a", 100,
        [new("tool-result-42", AiContextSourceKind.ToolResult, "secret-bearing runtime payload", 10, 0)]);

        var manifest = new DeterministicAiContextAssembler().Assemble(request);

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal("tool-result-42", entry.SourceId);
        Assert.Equal(AiContextSourceKind.ToolResult, entry.Kind);
        Assert.DoesNotContain("secret-bearing", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableEngine_PersistsCheckpointWithoutRawSourceContent()
    {
        var store = new CapturingCheckpointStore();
        var engine = new DurableAiContextEngine(new DeterministicAiContextAssembler(), store);
        var request = new AiContextRequest("tenant-a", "company-a", "task-a", "model-a", 100,
        [new("tool-result-42", AiContextSourceKind.ToolResult, "secret-bearing runtime payload", 10, 0)]);

        var checkpoint = await engine.AssembleAndCheckpointAsync(
            "checkpoint-1", new DateTimeOffset(2026, 9, 21, 16, 0, 0, TimeSpan.Zero), request);

        Assert.Same(checkpoint, store.Saved);
        Assert.Equal("checkpoint-1", checkpoint.CheckpointId);
        Assert.Equal("tenant-a", checkpoint.Manifest.TenantId);
        Assert.DoesNotContain("secret-bearing", checkpoint.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableEngine_PropagatesPersistenceFailure()
    {
        var engine = new DurableAiContextEngine(new DeterministicAiContextAssembler(), new FailingCheckpointStore());
        var request = new AiContextRequest("tenant-a", "company-a", "task-a", "model-a", 100,
        [new("policy", AiContextSourceKind.Policy, "policy", 10, 0, true)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.AssembleAndCheckpointAsync(
            "checkpoint-1", new DateTimeOffset(2026, 9, 21, 16, 0, 0, TimeSpan.Zero), request));
    }

    [Fact]
    public async Task DurableEngine_RejectsNonUtcCheckpointBeforePersistence()
    {
        var store = new CapturingCheckpointStore();
        var engine = new DurableAiContextEngine(new DeterministicAiContextAssembler(), store);
        var request = new AiContextRequest("tenant-a", "company-a", "task-a", "model-a", 100,
        [new("policy", AiContextSourceKind.Policy, "policy", 10, 0, true)]);

        await Assert.ThrowsAsync<ArgumentException>(() => engine.AssembleAndCheckpointAsync(
            "checkpoint-1", new DateTimeOffset(2026, 9, 21, 23, 0, 0, TimeSpan.FromHours(7)), request));
        Assert.Null(store.Saved);
    }

    private sealed class CapturingCheckpointStore : IAiContextCheckpointStore
    {
        public AiContextCheckpoint? Saved { get; private set; }
        public Task SaveAsync(AiContextCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            Saved = checkpoint;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingCheckpointStore : IAiContextCheckpointStore
    {
        public Task SaveAsync(AiContextCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("durable store unavailable"));
    }
}
