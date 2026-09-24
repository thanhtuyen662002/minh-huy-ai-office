using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerAiCreditReservationTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", 7);

    [Fact]
    public void Decide_AllowsReservationAgainstMatchingActiveHoldSnapshot()
    {
        var decision = CustomerAiCreditReservation.Decide("res-b", Admission(10, 2, 4, 3), [Evidence("res-a", 4)]);
        Assert.True(decision.Allowed); Assert.False(decision.IsReplay); Assert.Equal(4, decision.ActiveReservedAiCredits); Assert.Equal(9, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_RejectsActiveEvidenceThatDriftedFromAdmissionSnapshot() =>
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditReservation.Decide("res-b", Admission(10, 2, 3, 2), [Evidence("res-a", 4)]));

    [Fact]
    public void Decide_ExactReplayExcludesOwnReservationFromAdmissionSnapshot()
    {
        var existing = Evidence("res-a", 3); var other = Evidence("res-b", 2);
        var decision = CustomerAiCreditReservation.Decide("res-a", Admission(10, 2, 2, 3), [other, existing, existing]);
        Assert.True(decision.Allowed); Assert.True(decision.IsReplay); Assert.Equal(2, decision.ActiveReservedAiCredits); Assert.Equal(7, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_RejectsConflictingReservationReplay() =>
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditReservation.Decide("res-a", Admission(10, 2, 0, 3), [Evidence("res-a", 2)]));

    [Fact]
    public void Decide_RejectsStaleCrossCompanyAndPricingEvidence()
    {
        var stale = Evidence("res-a", 1) with { Authority = Authority with { AuthorityVersion = 6 } };
        var foreign = Evidence("res-a", 1) with { Authority = Authority with { CompanyId = "company-b" } };
        var pricing = Evidence("res-a", 1) with { PricingPolicyVersion = 2 };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditReservation.Decide("res-b", Admission(10, 1, 1, 1), [stale]));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditReservation.Decide("res-b", Admission(10, 1, 1, 1), [foreign]));
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditReservation.Decide("res-b", Admission(10, 1, 1, 1), [pricing]));
    }

    [Fact]
    public void Decide_RejectsDeniedOrInconsistentAdmission()
    {
        var denied = Admission(2, 2, 0, 1) with { Allowed = false };
        var inconsistent = Admission(10, 2, 1, 1) with { ProjectedAiCredits = 9 };
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditReservation.Decide("res-a", denied, []));
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditReservation.Decide("res-a", inconsistent, [Evidence("res-b", 1)]));
    }

    [Fact]
    public void Decide_IsDeterministicAcrossEvidenceOrder()
    {
        var a = Evidence("res-a", 2); var b = Evidence("res-b", 1);
        var first = CustomerAiCreditReservation.Decide("res-c", Admission(10, 1, 3, 2), [a, b]);
        var second = CustomerAiCreditReservation.Decide("res-c", Admission(10, 1, 3, 2), [b, a]);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Decide_FailsClosedOnAdmissionAndEvidenceOverflow()
    {
        var admissionOverflow = new CustomerAiCreditAdmissionDecision(Authority, "credits-v1", 3, long.MaxValue, long.MaxValue, 1, 0, long.MaxValue, true);
        Assert.Throws<OverflowException>(() => CustomerAiCreditReservation.Decide("res-c", admissionOverflow, [Evidence("res-a", 1)]));
        Assert.Throws<OverflowException>(() => CustomerAiCreditReservation.Decide("res-c", Admission(long.MaxValue, 0, long.MaxValue, 0), [Evidence("res-a", long.MaxValue - 1), Evidence("res-b", 2)]));
    }

    private static CustomerAiCreditAdmissionDecision Admission(long limit, long used, long active, long requested)
    {
        var projected = checked(checked(used + active) + requested);
        return new(Authority, "credits-v1", 3, limit, used, active, requested, projected, projected <= limit);
    }

    private static CustomerAiCreditReservationEvidence Evidence(string id, long credits) => new(id, Authority, "credits-v1", 3, credits);
}
