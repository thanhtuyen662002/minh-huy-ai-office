using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerAuditProjectionTests
{
    private readonly CustomerAuditAuthority authority = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private CustomerAuditEvent Event(Guid id, long version, DateTimeOffset? occurredAt = null, string evidence = "evidence:opaque") =>
        new(authority, id, version, occurredAt ?? Now, "user-1", "task.read", "task/1", evidence);

    [Fact]
    public void Projection_is_deterministic_and_provider_independent()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var rows = CustomerAuditProjection.Project(authority, new[] { Event(secondId, 2), Event(firstId, 1) });
        Assert.Equal(new[] { firstId, secondId }, rows.Select(x => x.EventId));
    }

    [Fact]
    public void Cross_company_event_and_cursor_fail_closed()
    {
        var foreign = authority with { CompanyId = Guid.NewGuid() };
        var foreignEvent = Event(Guid.NewGuid(), 1) with { Authority = foreign };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAuditProjection.Project(authority, new[] { foreignEvent }));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAuditProjection.Project(authority, Array.Empty<CustomerAuditEvent>(), new CustomerAuditCursor(foreign, Now, 1, Guid.NewGuid())));
    }

    [Fact]
    public void Exact_duplicate_is_idempotent_but_conflicting_evidence_is_rejected()
    {
        var id = Guid.NewGuid();
        var original = Event(id, 1);
        Assert.Single(CustomerAuditProjection.Project(authority, new[] { original, original }));
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(authority, new[] { original, original with { EvidenceReference = "evidence:other" } }));
    }

    [Fact]
    public void Cursor_filters_monotonically_and_cannot_move_backward()
    {
        var first = Event(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1);
        var second = Event(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2);
        var cursor = new CustomerAuditCursor(authority, first.OccurredAt, 1, first.EventId);
        var rows = CustomerAuditProjection.Project(authority, new[] { first, second }, cursor);
        Assert.Equal(second.EventId, Assert.Single(rows).EventId);
        var next = CustomerAuditProjection.NextCursor(authority, rows, cursor);
        Assert.Equal(2, next.Version);
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.NextCursor(authority, new[] { new CustomerAuditRow(first.EventId, 1, Now.AddTicks(-1), "actor", "read", "resource", "evidence") }, next));
    }

    [Fact]
    public void Cursor_uses_the_same_chronological_order_as_projection()
    {
        var earlierId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var laterId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var earlier = Event(earlierId, 20, Now);
        var later = Event(laterId, 10, Now.AddMinutes(1));

        var firstPage = CustomerAuditProjection.Project(authority, new[] { later, earlier });
        Assert.Equal(new[] { earlierId, laterId }, firstPage.Select(x => x.EventId));

        var cursor = CustomerAuditProjection.NextCursor(authority, new[] { firstPage[0] });
        var secondPage = CustomerAuditProjection.Project(authority, new[] { later, earlier }, cursor);
        Assert.Equal(laterId, Assert.Single(secondPage).EventId);
    }

    [Fact]
    public void Read_requires_exact_authority_and_explicit_capability()
    {
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAuditProjection.AuthorizeRead(authority, authority, Array.Empty<string>()));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAuditProjection.AuthorizeRead(authority, authority with { CompanyId = Guid.NewGuid() }, new[] { CustomerAuditProjection.ReadCapability }));
        CustomerAuditProjection.AuthorizeRead(authority, authority, new[] { CustomerAuditProjection.ReadCapability });
    }

    [Fact]
    public void Secret_bearing_evidence_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(authority, new[] { Event(Guid.NewGuid(), 1, evidence: "password=unsafe") }));
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(authority, new[] { Event(Guid.NewGuid(), 1, evidence: "secret=unsafe") }));
    }

    [Fact]
    public void Malformed_authority_event_and_cursor_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(new CustomerAuditAuthority(Guid.Empty, authority.CompanyId), Array.Empty<CustomerAuditEvent>()));
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(authority, new[] { Event(Guid.Empty, 1) }));
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(authority, new[] { Event(Guid.NewGuid(), 0) }));
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(authority, Array.Empty<CustomerAuditEvent>(), new CustomerAuditCursor(authority, DateTimeOffset.MinValue, -1, Guid.Empty)));
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(authority, Array.Empty<CustomerAuditEvent>(), new CustomerAuditCursor(authority, Now, 0, Guid.Empty)));
        Assert.Throws<InvalidOperationException>(() => CustomerAuditProjection.Project(authority, Array.Empty<CustomerAuditEvent>(), new CustomerAuditCursor(authority, Now, 1, Guid.Empty)));
    }
}
