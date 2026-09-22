using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class RuntimeSlaSchedulingTests
{
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid company = Guid.NewGuid();

    [Fact]
    public void Requested_priority_cannot_exceed_authorized_ceiling()
    {
        var (policy, authority) = Policy(40);
        var result = RuntimeSlaScheduler.Bind(policy, authority, Guid.NewGuid(), RuntimeTaskClass.BusinessCritical, 999, DateTimeOffset.UnixEpoch);
        Assert.Equal(40, result.EffectivePriority);
    }

    [Fact]
    public void Cross_company_authority_is_rejected()
    {
        var (policy, authority) = Policy(80);
        var foreign = authority with { CompanyId = Guid.NewGuid() };
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeSlaScheduler.Bind(policy, foreign, Guid.NewGuid(), RuntimeTaskClass.Standard, 1, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Stale_policy_version_is_rejected()
    {
        var (policy, authority) = Policy(80);
        Assert.Throws<InvalidOperationException>(() => RuntimeSlaScheduler.Bind(policy, authority with { PolicyVersion = 1 }, Guid.NewGuid(), RuntimeTaskClass.Standard, 1, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Malformed_policy_and_broadened_authority_fail_closed()
    {
        var (policy, authority) = Policy(80);
        Assert.Throws<InvalidOperationException>(() => RuntimeSlaScheduler.Bind(policy with { PolicyId = " " }, authority, Guid.NewGuid(), RuntimeTaskClass.Standard, 1, DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidOperationException>(() => RuntimeSlaScheduler.Bind(policy, authority with { PriorityCeiling = 81 }, Guid.NewGuid(), RuntimeTaskClass.Standard, 1, DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidOperationException>(() => RuntimeSlaScheduler.Bind(policy with { StandardTarget = TimeSpan.Zero }, authority, Guid.NewGuid(), RuntimeTaskClass.Standard, 1, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => RuntimeSlaScheduler.Bind(policy, authority, Guid.Empty, RuntimeTaskClass.Standard, 1, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Unknown_classification_fails_closed()
    {
        var (policy, authority) = Policy(80);
        Assert.Throws<InvalidOperationException>(() => RuntimeSlaScheduler.Bind(policy, authority, Guid.NewGuid(), (RuntimeTaskClass)999, 1, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Resume_preserves_exact_authority_snapshot()
    {
        var (policy, authority) = Policy(80);
        var original = RuntimeSlaScheduler.Bind(policy, authority, Guid.NewGuid(), RuntimeTaskClass.CustomerInteractive, 30, DateTimeOffset.UnixEpoch);
        Assert.Same(original, RuntimeSlaScheduler.Resume(original));
    }

    [Fact]
    public void Equal_priority_and_deadline_order_by_durable_task_identity()
    {
        var (policy, authority) = Policy(80);
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var first = RuntimeSlaScheduler.Bind(policy, authority, firstId, RuntimeTaskClass.Standard, 10, DateTimeOffset.UnixEpoch);
        var second = RuntimeSlaScheduler.Bind(policy, authority, secondId, RuntimeTaskClass.Standard, 10, DateTimeOffset.UnixEpoch);
        Assert.True(RuntimeSlaScheduler.Compare(first, second) < 0);
    }

    [Fact]
    public void Explicit_rebind_requires_newer_policy_and_same_scope()
    {
        var (policy, authority) = Policy(80);
        var current = RuntimeSlaScheduler.Bind(policy, authority, Guid.NewGuid(), RuntimeTaskClass.Standard, 10, DateTimeOffset.UnixEpoch);
        var newer = policy with { Version = 3, PriorityCeiling = 90 };
        var newerAuthority = authority with { PolicyVersion = 3, PriorityCeiling = 90 };
        var rebound = RuntimeSlaScheduler.Rebind(current, newer, newerAuthority, 70, DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.Equal(3, rebound.PolicyVersion);
        Assert.Equal(70, rebound.EffectivePriority);
        Assert.Equal(current.TaskId, rebound.TaskId);
        Assert.Throws<InvalidOperationException>(() => RuntimeSlaScheduler.Rebind(current, policy, authority, 20, DateTimeOffset.UnixEpoch));
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeSlaScheduler.Rebind(current, newer with { CompanyId = Guid.NewGuid() }, newerAuthority with { CompanyId = Guid.NewGuid() }, 20, DateTimeOffset.UnixEpoch));
    }

    private (RuntimeSlaPolicy Policy, RuntimeSchedulingAuthority Authority) Policy(int ceiling)
    {
        var policy = new RuntimeSlaPolicy(tenant, company, "default", 2, ceiling,
            TimeSpan.FromHours(4), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));
        var authority = new RuntimeSchedulingAuthority(tenant, company, "default", 2, ceiling);
        return (policy, authority);
    }
}
