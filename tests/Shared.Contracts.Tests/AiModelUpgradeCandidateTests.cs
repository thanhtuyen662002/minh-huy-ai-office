using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class AiModelUpgradeCandidateTests
{
    [Fact]
    public void Activate_RequiresExactGreenEvidenceAndReleaseGates()
    {
        var candidate = Candidate();
        var activation = candidate.Activate("tenant", "company", Evidence(), releaseGatesPassed: true);

        Assert.Equal("provider-new", activation.ProviderId);
        Assert.Equal("model-new", activation.ModelId);
    }

    [Fact]
    public void Activate_RejectsStaleOrMismatchedEvidence()
    {
        var candidate = Candidate();

        Assert.Throws<InvalidOperationException>(() => candidate.Activate("tenant", "company", Evidence() with { CandidateVersion = "v0" }, true));
        Assert.Throws<InvalidOperationException>(() => candidate.Activate("tenant", "company", Evidence() with { SourceCommit = "old" }, true));
        Assert.Throws<InvalidOperationException>(() => candidate.Activate("tenant", "company", Evidence() with { Passed = false }, true));
        Assert.Throws<InvalidOperationException>(() => candidate.Activate("tenant", "company", Evidence(), false));
    }

    [Fact]
    public void Activate_RejectsCrossCompanyAuthority()
    {
        Assert.Throws<InvalidOperationException>(() => Candidate().Activate("tenant", "other-company", Evidence(), true));
    }

    [Fact]
    public void Validate_RejectsUnhealthyIncompatibleOrRegressiveCandidate()
    {
        var current = Model("provider-old", "model-old", AiCapability.Reasoning, 2, 3, 200);

        Assert.Throws<InvalidOperationException>(() => (Candidate() with { Proposed = Model("p", "unhealthy", AiCapability.Reasoning, 2, 3, 200, false) }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Candidate() with { Proposed = Model("p", "vision", AiCapability.Vision, 2, 3, 200) }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Candidate() with { Proposed = Model("p", "low-tier", AiCapability.Reasoning, 1, 3, 200) }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Candidate() with { Current = current, Proposed = Model("p", "costly", AiCapability.Reasoning, 2, 4, 200) }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Candidate() with { Current = current, Proposed = Model("p", "slow", AiCapability.Reasoning, 2, 3, 201) }).Validate());
    }

    [Fact]
    public void Activation_DoesNotMutateInFlightTaskModelPin()
    {
        var pin = new AiTaskModelPin("tenant", "company", "task-1", "provider-old", "model-old");
        var activation = Candidate().Activate("tenant", "company", Evidence(), true);

        Assert.Equal("provider-old", pin.ProviderId);
        Assert.Equal("model-old", pin.ModelId);
        Assert.Equal("provider-new", activation.ProviderId);
        pin.AssertAuthority("tenant", "company");
    }

    [Fact]
    public void Activation_RollbackSelectsExactPreviousModelWithinAuthority()
    {
        var activation = Candidate().Activate("tenant", "company", Evidence(), true);

        var rollback = activation.Rollback("tenant", "company");

        Assert.Equal("provider-old", rollback.ProviderId);
        Assert.Equal("model-old", rollback.ModelId);
        Assert.Throws<InvalidOperationException>(() => activation.Rollback("tenant", "other-company"));
    }

    private static AiModelUpgradeCandidate Candidate() => new(
        "candidate-1",
        "v1",
        "abc123",
        "tenant",
        "company",
        AiCapability.Reasoning,
        Model("provider-old", "model-old", AiCapability.Reasoning, 2, 3, 200),
        Model("provider-new", "model-new", AiCapability.Reasoning, 3, 2, 150));

    private static AiModelUpgradeEvalEvidence Evidence() => new("candidate-1", "v1", "abc123", "eval-1", true);

    private static AiModelProfile Model(string provider, string model, AiCapability capability, int tier, decimal cost, int latency, bool healthy = true) =>
        new(provider, model, new HashSet<AiCapability> { capability }, tier, cost, latency, healthy);
}
