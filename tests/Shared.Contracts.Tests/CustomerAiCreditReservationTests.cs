using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class CustomerAiCreditReservationTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", "user-a", 7);

    [Fact]
    public void Decide_AllowsReservationWithinAdmission()
    {
        var decision = CustomerAiCreditReservation.Decide("res-c", Admission(10, 2, 3), []);

        Assert.True(decision.Allowed);
        Assert.Equal(3, decision.ReservedAiCredits);
    }

    [Fact]
    public void Decide_FailsClosedWhenAdmissionDenied()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CustomerAiCreditReservation.Decide("res-c", Admission(4, 2, 3), []));
    }

    [Fact]
    public void Decide_FailsClosedOnAuthorityMismatch()
    {
        var foreign = new CustomerAiCreditReservationEvidence(
            "res-a",
            new CustomerBillingAuthority("tenant-a", "company-b", "user-a", 7),
            "credits-v1",
            3,
            1);

        Assert.Throws<InvalidOperationException>(() =>
            CustomerAiCreditReservation.Decide("res-c", Admission(10, 1, 2), [foreign]));
    }

    [Fact]
    public void Decide_FailsClosedOnPricingMismatch()
    {
        var stale = new CustomerAiCreditReservationEvidence("res-a", Authority, "credits-v1", 2, 1);

        Assert.Throws<InvalidOperationException>(() =>
            CustomerAiCreditReservation.Decide("res-c", Admission(10, 1, 2), [stale]));
    }

    [Fact]
    public void Decide_FailsClosedOnConflictingDuplicateReservation()
    {
        var a = Evidence("res-a", 1);
        var conflict = Evidence("res-a", 2);

        Assert.Throws<InvalidOperationException>(() =>
            CustomerAiCreditReservation.Decide("res-c", Admission(10, 1, 2), [a, conflict]));
    }

    [Fact]
    public void Decide_TreatsExactDuplicateReservationAsIdempotent()
    {
        var a = Evidence("res-a", 1);
        var decision = CustomerAiCreditReservation.Decide("res-c", Admission(10, 1, 2), [a, a]);

        Assert.True(decision.Allowed);
        Assert.Equal(2, decision.ReservedAiCredits);
    }

    [Fact]
    public void Decide_IsOrderIndependent()
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
        new(Authority, "credits-v1", 3, limit, used, 0, requested, checked(used + requested), used + requested <= limit);

    private static CustomerAiCreditReservationEvidence Evidence(string id, long credits) =>
        new(id, Authority, "credits-v1", 3, credits);
}
