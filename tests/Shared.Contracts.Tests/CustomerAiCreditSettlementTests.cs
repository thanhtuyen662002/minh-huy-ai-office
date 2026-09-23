using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerAiCreditSettlementTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", 7);

    [Fact]
    public void Decide_SettlesFullReservation()
    {
        var decision = CustomerAiCreditSettlement.Decide("set-a", Reservation(3), 3, []);
        Assert.False(decision.IsReplay);
        Assert.Equal(3, decision.Settlement.SettledAiCredits);
        Assert.Equal(0, decision.Settlement.ReleasedAiCredits);
    }

    [Fact]
    public void Decide_PartialSettlementReleasesUnusedCapacity()
    {
        var decision = CustomerAiCreditSettlement.Decide("set-a", Reservation(5), 2, []);
        Assert.Equal(2, decision.Settlement.SettledAiCredits);
        Assert.Equal(3, decision.Settlement.ReleasedAiCredits);
    }

    [Fact]
    public void Decide_ExactReplayIsIdempotent()
    {
        var existing = Evidence("set-a", "res-a", 2, 3);
        var decision = CustomerAiCreditSettlement.Decide("set-a", Reservation(5), 2, [existing, existing]);
        Assert.True(decision.IsReplay);
        Assert.Equal(existing, decision.Settlement);
    }

    [Fact]
    public void Decide_RejectsConflictingSettlementIdentityReplay()
    {
        var existing = Evidence("set-a", "res-a", 1, 4);
        Assert.Throws<InvalidOperationException>(() =>
            CustomerAiCreditSettlement.Decide("set-a", Reservation(5), 2, [existing]));
    }

    [Fact]
    public void Decide_RejectsSecondSettlementForReservation()
    {
        var existing = Evidence("set-old", "res-a", 2, 3);
        Assert.Throws<InvalidOperationException>(() =>
            CustomerAiCreditSettlement.Decide("set-new", Reservation(5), 2, [existing]));
    }

    [Fact]
    public void Decide_RejectsStaleCrossCompanyAndPolicyEvidence()
    {
        var stale = Evidence("set-old", "res-b", 1, 4) with { Authority = Authority with { AuthorityVersion = 6 } };
        var foreign = Evidence("set-old", "res-b", 1, 4) with { Authority = Authority with { CompanyId = "company-b" } };
        var policy = Evidence("set-old", "res-b", 1, 4) with { PricingPolicyVersion = 2 };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditSettlement.Decide("set-a", Reservation(5), 2, [stale]));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditSettlement.Decide("set-a", Reservation(5), 2, [foreign]));
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditSettlement.Decide("set-a", Reservation(5), 2, [policy]));
    }

    [Fact]
    public void Decide_RejectsOverSettlementAndDeniedReservation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CustomerAiCreditSettlement.Decide("set-a", Reservation(3), 4, []));
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditSettlement.Decide("set-a", Reservation(3) with { Allowed = false }, 2, []));
    }

    [Fact]
    public void Decide_IsDeterministicAcrossUnrelatedEvidenceOrder()
    {
        var a = Evidence("set-x", "res-x", 1, 4);
        var b = Evidence("set-y", "res-y", 2, 3);
        var first = CustomerAiCreditSettlement.Decide("set-a", Reservation(5), 2, [a, b]);
        var second = CustomerAiCreditSettlement.Decide("set-a", Reservation(5), 2, [b, a]);
        Assert.Equal(first, second);
    }

    private static CustomerAiCreditReservationDecision Reservation(long reserved)
    {
        var evidence = new CustomerAiCreditReservationEvidence("res-a", Authority, "credits-v1", 3, reserved);
        return new(evidence, 2, 1, checked(3 + reserved), 20, true, false);
    }

    private static CustomerAiCreditSettlementEvidence Evidence(string settlementId, string reservationId, long settled, long released) =>
        new(settlementId, reservationId, Authority, "credits-v1", 3, settled, released);
}
