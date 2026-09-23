using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerAiCreditReservationTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", 7);

    [Fact]
    public void Decide_AllowsReservationWithinRemainingCapacity()
    {
        var decision = CustomerAiCreditReservation.Decide("res-b", Admission(10, 2, 3), [Evidence("res-a", 4)]);
        Assert.True(decision.Allowed);
        Assert.False(decision.IsReplay);
        Assert.Equal(9, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_DeniesConcurrentReservationThatWouldOversubscribeCapacity()
    {
        var decision = CustomerAiCreditReservation.Decide("res-b", Admission(10, 4, 3), [Evidence("res-a", 4)]);
        Assert.False(decision.Allowed);
        Assert.Equal(11, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_ExactReplayIsIdempotent()
    {
        var existing = Evidence("res-a", 3);
        var decision = CustomerAiCreditReservation.Decide("res-a", Admission(10, 2, 3), [existing, existing]);
        Assert.True(decision.Allowed);
        Assert.True(decision.IsReplay);
        Assert.Equal(5, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_RejectsConflictingReservationReplay()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CustomerAiCreditReservation.Decide("res-a", Admission(10, 2, 3), [Evidence("res-a", 2)]));
    }

    [Fact]
    public void Decide_RejectsStaleAndCrossCompanyEvidence()
    {
        var stale = Evidence("res-a", 1) with { Authority = Authority with { AuthorityVersion = 6 } };
        var foreign = Evidence("res-a", 1) with { Authority = Authority with { CompanyId = "company-b" } };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditReservation.Decide("res-b", Admission(10, 1, 1), [stale]));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditReservation.Decide("res-b", Admission(10, 1, 1), [foreign]));
    }

    [Fact]
    public void Decide_RejectsDeniedOrInconsistentAdmission()
    {
        var denied = Admission(2, 2, 1) with { Allowed = false };
        var inconsistent = Admission(10, 2, 1) with { ProjectedAiCredits = 9 };
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditReservation.Decide("res-a", denied, []));
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditReservation.Decide("res-a", inconsistent, []));
    }

    [Fact]
    public void Decide_IsDeterministicAcrossEvidenceOrder()
    {
        var a = Evidence("res-a", 2);
        var b = Evidence("res-b", 1);
        var first = CustomerAiCreditReservation.Decide("res-c", Admission(10, 1, 2), [a, b]);
        var second = CustomerAiCreditReservation.Decide("res-c", Admission(10, 1, 2), [b, a]);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Decide_FailsClosedOnReservationOverflow()
    {
        Assert.Throws<OverflowException>(() =>
            CustomerAiCreditReservation.Decide("res-c", Admission(long.MaxValue, long.MaxValue - 2, 1), [Evidence("res-a", 2)]));
    }

    private static CustomerAiCreditAdmissionDecision Admission(long limit, long used, long requested) =>
        new(Authority, "credits-v1", 3, limit, used, requested, checked(used + requested), used + requested <= limit);

    private static CustomerAiCreditReservationEvidence Evidence(string id, long credits) =>
        new(id, Authority, "credits-v1", 3, credits);
}