using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerAiCreditActiveReservationsTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", 7);

    [Fact]
    public void Project_RemovesSettledReservationsAndKeepsUnsettledCapacity()
    {
        var projection = CustomerAiCreditActiveReservations.Project(
            Authority, "credits-v1", 3,
            [Reservation("res-b", 4), Reservation("res-a", 5)],
            [Settlement("set-a", "res-a", 2, 3)]);

        Assert.Equal(4, projection.ActiveReservedAiCredits);
        Assert.Collection(projection.ActiveReservations, x => Assert.Equal("res-b", x.ReservationId));
    }

    [Fact]
    public void Project_PartialSettlementReleasesEntireReservationHold()
    {
        var projection = CustomerAiCreditActiveReservations.Project(
            Authority, "credits-v1", 3,
            [Reservation("res-a", 5)],
            [Settlement("set-a", "res-a", 2, 3)]);

        Assert.Empty(projection.ActiveReservations);
        Assert.Equal(0, projection.ActiveReservedAiCredits);
    }

    [Fact]
    public void Project_DeduplicatesExactReplayEvidence()
    {
        var reservation = Reservation("res-a", 5);
        var settlement = Settlement("set-a", "res-a", 2, 3);
        var projection = CustomerAiCreditActiveReservations.Project(
            Authority, "credits-v1", 3,
            [reservation, reservation], [settlement, settlement]);

        Assert.Empty(projection.ActiveReservations);
    }

    [Fact]
    public void Project_RejectsConflictingReservationAndSettlementReplay()
    {
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditActiveReservations.Project(
            Authority, "credits-v1", 3,
            [Reservation("res-a", 5), Reservation("res-a", 4)], []));

        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditActiveReservations.Project(
            Authority, "credits-v1", 3,
            [Reservation("res-a", 5), Reservation("res-b", 5)],
            [Settlement("set-a", "res-a", 2, 3), Settlement("set-a", "res-b", 2, 3)]));
    }

    [Fact]
    public void Project_RejectsOrphanAndNonReconcilingSettlement()
    {
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditActiveReservations.Project(
            Authority, "credits-v1", 3, [], [Settlement("set-a", "res-a", 2, 3)]));

        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditActiveReservations.Project(
            Authority, "credits-v1", 3,
            [Reservation("res-a", 5)], [Settlement("set-a", "res-a", 2, 2)]));
    }

    [Fact]
    public void Project_RejectsStaleCrossCompanyAndPolicyEvidence()
    {
        var stale = Reservation("res-a", 5) with { Authority = Authority with { AuthorityVersion = 6 } };
        var foreign = Reservation("res-a", 5) with { Authority = Authority with { CompanyId = "company-b" } };
        var policy = Reservation("res-a", 5) with { PricingPolicyVersion = 2 };

        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditActiveReservations.Project(Authority, "credits-v1", 3, [stale], []));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditActiveReservations.Project(Authority, "credits-v1", 3, [foreign], []));
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditActiveReservations.Project(Authority, "credits-v1", 3, [policy], []));
    }

    [Fact]
    public void Project_IsDeterministicAcrossInputOrder()
    {
        var a = Reservation("res-a", 2);
        var b = Reservation("res-b", 3);
        var first = CustomerAiCreditActiveReservations.Project(Authority, "credits-v1", 3, [b, a], []);
        var second = CustomerAiCreditActiveReservations.Project(Authority, "credits-v1", 3, [a, b], []);

        Assert.Equal(first.ActiveReservedAiCredits, second.ActiveReservedAiCredits);
        Assert.Equal(first.ActiveReservations.Select(x => x.ReservationId), second.ActiveReservations.Select(x => x.ReservationId));
    }

    [Fact]
    public void Project_FailsClosedOnActiveCapacityOverflow()
    {
        Assert.Throws<OverflowException>(() => CustomerAiCreditActiveReservations.Project(
            Authority, "credits-v1", 3,
            [Reservation("res-a", long.MaxValue), Reservation("res-b", 1)], []));
    }

    private static CustomerAiCreditReservationEvidence Reservation(string id, long credits) =>
        new(id, Authority, "credits-v1", 3, credits);

    private static CustomerAiCreditSettlementEvidence Settlement(string id, string reservationId, long settled, long released) =>
        new(id, reservationId, Authority, "credits-v1", 3, settled, released);
}
