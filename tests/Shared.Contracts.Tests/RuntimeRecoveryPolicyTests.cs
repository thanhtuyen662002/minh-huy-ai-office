using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class RuntimeRecoveryPolicyTests
{
    [Fact]
    public void Healing_AllowsOnlyBoundedPreAuthorizedReversibleRecovery()
    {
        var decision = RuntimeRecoveryPolicy.EvaluateHealing(Healing(), "tenant", "company");

        Assert.True(decision.HealingAllowed);
        Assert.False(decision.ImprovementRequired);
        Assert.Equal("task-1", decision.TaskId);
    }

    [Fact]
    public void Healing_RejectsVersionedMutationAndCannotReclassifyIt()
    {
        var request = Healing() with { Kind = RuntimeRecoveryKind.ChangeModel };

        var decision = RuntimeRecoveryPolicy.EvaluateHealing(request, "tenant", "company");

        Assert.False(decision.HealingAllowed);
        Assert.True(decision.ImprovementRequired);
    }

    [Fact]
    public void Healing_FailsClosedAcrossCompanyAuthority()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RuntimeRecoveryPolicy.EvaluateHealing(Healing(), "tenant", "other-company"));
    }

    [Fact]
    public void Healing_EscalatesUnsafeOrRepeatedRecoveryInsteadOfRetryLoop()
    {
        Assert.True(RuntimeRecoveryPolicy.EvaluateHealing(Healing() with { PreAuthorized = false }, "tenant", "company").ImprovementRequired);
        Assert.True(RuntimeRecoveryPolicy.EvaluateHealing(Healing() with { Reversible = false }, "tenant", "company").ImprovementRequired);
        Assert.True(RuntimeRecoveryPolicy.EvaluateHealing(Healing() with { PriorHealingAttempts = RuntimeRecoveryPolicy.MaxAutomaticHealingAttempts }, "tenant", "company").ImprovementRequired);
    }

    [Fact]
    public void Improvement_RequiresVersionedCandidateAndAllMechanicalGates()
    {
        var request = Healing() with { Kind = RuntimeRecoveryKind.ChangeWorkflow };
        var evidence = Evidence();

        var accepted = RuntimeRecoveryPolicy.EvaluateImprovement(request, "tenant", "company", evidence);
        var rejected = RuntimeRecoveryPolicy.EvaluateImprovement(request, "tenant", "company", evidence with { EvalPassed = false });

        Assert.False(accepted.HealingAllowed);
        Assert.False(accepted.ImprovementRequired);
        Assert.True(rejected.ImprovementRequired);
    }

    [Fact]
    public void Improvement_DoesNotAcceptOrdinaryHealingKind()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RuntimeRecoveryPolicy.EvaluateImprovement(Healing(), "tenant", "company", Evidence()));
    }

    private static RuntimeRecoveryRequest Healing() => new(
        "tenant", "company", "task-1", "recovery-1",
        RuntimeRecoveryKind.RetryTransientOperation, 0, true, true);

    private static SelfImprovementEvidence Evidence() => new(
        "candidate-1", "v1", "abc123", "eval-1", true, true, true);
}
