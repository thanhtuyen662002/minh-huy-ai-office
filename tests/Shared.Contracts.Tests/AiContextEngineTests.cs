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
}
