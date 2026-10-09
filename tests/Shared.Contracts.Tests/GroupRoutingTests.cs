using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class GroupRoutingTests
{
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid company = Guid.NewGuid();
    private readonly Guid service = Guid.NewGuid();
    private GroupServiceAuthentication Authentication => new(service, 7);
    private GroupServicePrincipal Principal => new(tenant, company, service, 7, true);
    private GroupServiceGrant Grant(Guid binding, GroupServiceCapability capability) => new(tenant, company, service, binding, capability, 9, true);
    private SourceGroupBinding Source(string id = "customer-A") => new(new(tenant, company, Guid.NewGuid()),
        Guid.NewGuid(), new("synthetic", "account-1", id), "Same display name", 3, 0, true);
    private TechnicalDestinationBinding Destination() => new(tenant, company, Guid.NewGuid(),
        Guid.NewGuid(), new("synthetic", "account-2", "IT-group"), "Same display name", 5, true);
    private static GroupRouteSnapshot Snapshot(SourceGroupBinding source, TechnicalDestinationBinding destination) => new(source,
        destination, new(source.Scope, destination.Id, 11, source.Version, destination.Version, true),
        new(source.Scope, destination.Id, 11, 13, true));

    [Theory]
    [InlineData("group", "Group")]
    [InlineData("group", "group ")]
    [InlineData("group", " group")]
    [InlineData("é", "e\u0301")]
    [InlineData("account", "account\uFEFF")]
    public void External_identity_and_index_distinguish_ordinal_aliases(string a, string b)
    {
        var left = new GroupExternalIdentity("synthetic", "account", a);
        var right = left with { GroupId = b };
        Assert.NotEqual(left, right);
        Assert.NotEqual(left.IndexKey(), right.IndexKey());
        Assert.False(left.IsSameProviderGroup(right));
    }

    [Fact]
    public void Length_prefix_prevents_identity_boundary_collision()
    {
        Assert.NotEqual(new GroupExternalIdentity("synthetic", "a", "bc").IndexKey(),
            new GroupExternalIdentity("synthetic", "ab", "c").IndexKey());
        var exact = new GroupExternalIdentity("synthetic", "account ", " group😀 ");
        Assert.Equal(exact.IndexKey(), (exact with { }).IndexKey());
        Assert.Equal(" group😀 ", exact.GroupId);
        // Independent Python hashlib/struct little-endian UTF8 vector.
        Assert.Equal("E6A0749FB48B2CC0E601E3412D462580886A13C3E34971A794B21C63C5E68599", exact.IndexKey());
    }

    [Fact]
    public void Empty_and_malformed_scalar_id_is_refused()
    {
        // Construct raw UTF16 inside the test: discovery serialization can replace unpaired scalars.
        foreach (var value in new[] { string.Empty, new string((char)0xD800, 1), new string((char)0xDC00, 1) })
            Assert.Throws<InvalidOperationException>(() => new GroupExternalIdentity("synthetic", "account", value).IndexKey());
    }

    [Fact]
    public void Opaque_ids_have_a_fixed_maximum_without_trimming()
    {
        new GroupExternalIdentity("synthetic", "account", new string(' ', 256)).Validate();
        Assert.Throws<InvalidOperationException>(() => new GroupExternalIdentity("synthetic", "account", new string('x', 257)).Validate());
    }

    [Theory]
    [InlineData("Synthetic")]
    [InlineData("synthetic ")]
    [InlineData("")]
    [InlineData("provider/other")]
    public void Backend_provider_identifier_is_canonical(string provider)
    {
        Assert.Throws<InvalidOperationException>(() => new GroupExternalIdentity(provider, "account", "group").Validate());
    }

    [Fact]
    public void Authorized_new_group_text_does_not_require_a_mention_or_a_route()
    {
        var source = Source();
        Assert.Equal(source.Scope, GroupRoutingPolicy.AuthorizeIngest(Authentication, Principal,
            Grant(source.Scope.SourceBindingId, GroupServiceCapability.Ingest), source, source.ExternalIdentity, true, false));
        Assert.Equal(source.Scope, GroupRoutingPolicy.AuthorizeExtraction(Authentication, Principal,
            Grant(source.Scope.SourceBindingId, GroupServiceCapability.Extract), source));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Dm_and_known_report_echo_are_refused_before_intake(bool isGroup, bool echo)
    {
        var source = Source();
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeIngest(Authentication, Principal,
            Grant(source.Scope.SourceBindingId, GroupServiceCapability.Ingest), source, source.ExternalIdentity, isGroup, echo));
    }

    [Theory]
    [InlineData("customer-a")]
    [InlineData("customer-A ")]
    [InlineData("another-group")]
    public void Observed_source_must_match_the_full_opaque_binding(string group)
    {
        var source = Source();
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeIngest(Authentication, Principal,
            Grant(source.Scope.SourceBindingId, GroupServiceCapability.Ingest), source, source.ExternalIdentity with { GroupId = group }, true, false));
    }

    [Theory]
    [InlineData(GroupServiceCapability.Ingest)]
    [InlineData(GroupServiceCapability.Notify)]
    public void Other_capability_does_not_authorize_extraction(GroupServiceCapability capability)
    {
        var source = Source();
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeExtraction(Authentication, Principal,
            Grant(source.Scope.SourceBindingId, capability), source));
    }

    [Fact]
    public void Fresh_service_credential_grant_scope_and_binding_are_all_required()
    {
        var source = Source();
        var grant = Grant(source.Scope.SourceBindingId, GroupServiceCapability.Extract);
        var badPrincipals = new[] { Principal with { IsEnabled = false }, Principal with { TenantId = Guid.NewGuid() },
            Principal with { CompanyId = Guid.NewGuid() }, Principal with { ServiceId = Guid.NewGuid() }, Principal with { CredentialEpoch = 8 } };
        foreach (var principal in badPrincipals)
            Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeExtraction(Authentication, principal, grant, source));
        var badGrants = new[] { grant with { IsEnabled = false }, grant with { TenantId = Guid.NewGuid() },
            grant with { CompanyId = Guid.NewGuid() }, grant with { ServiceId = Guid.NewGuid() }, grant with { BindingId = Guid.NewGuid() } };
        foreach (var invalid in badGrants)
            Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeExtraction(Authentication, Principal, invalid, source));
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeExtraction(Authentication, Principal, grant, source with { IsEnabled = false }));
        Assert.Throws<InvalidOperationException>(() => GroupRoutingPolicy.AuthorizeExtraction(Authentication, Principal, grant with { Version = 0 }, source));
    }

    [Fact]
    public void Two_same_named_sources_notify_one_distinct_it_destination_without_user_approval()
    {
        var first = Source();
        var second = Source("customer-B");
        var destination = Destination();
        var result = GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal,
            Grant(destination.Id, GroupServiceCapability.Notify), [Snapshot(first, destination), Snapshot(second, destination)],
            [first.ExternalIdentity, second.ExternalIdentity]);
        Assert.Equal(destination, result.Destination);
        Assert.Equal(2, result.Routes.Count);
        Assert.Equal(new[] { first.Scope.SourceBindingId, second.Scope.SourceBindingId }.Order(), result.Routes.Select(x => x.SourceBindingId));
        Assert.DoesNotContain(typeof(GroupServicePrincipal).GetProperties(), x => x.Name == "UserId");
        Assert.DoesNotContain(typeof(GroupNotificationAuthorization).GetProperties(), x => x.Name.Contains("Approval", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_source_route_and_audience_version_is_checked()
    {
        var source = Source();
        var destination = Destination();
        var snapshot = Snapshot(source, destination);
        var bad = new[] { snapshot with { Source = source with { IsEnabled = false } },
            snapshot with { Source = source with { Scope = source.Scope with { TenantId = Guid.NewGuid() } } },
            snapshot with { Source = source with { Scope = source.Scope with { CompanyId = Guid.NewGuid() } } },
            snapshot with { Route = snapshot.Route with { IsEnabled = false } },
            snapshot with { Route = snapshot.Route with { SourceBindingVersion = 4 } },
            snapshot with { Route = snapshot.Route with { DestinationBindingVersion = 6 } },
            snapshot with { Route = snapshot.Route with { DestinationBindingId = Guid.NewGuid() } },
            snapshot with { Route = snapshot.Route with { Source = Source().Scope } },
            snapshot with { AudienceGrant = snapshot.AudienceGrant with { IsEnabled = false } },
            snapshot with { AudienceGrant = snapshot.AudienceGrant with { RouteVersion = 12 } },
            snapshot with { AudienceGrant = snapshot.AudienceGrant with { Source = Source().Scope } },
            snapshot with { AudienceGrant = snapshot.AudienceGrant with { DestinationBindingId = Guid.NewGuid() } } };
        foreach (var invalid in bad)
            Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal,
                Grant(destination.Id, GroupServiceCapability.Notify), [invalid], [source.ExternalIdentity]));
        Assert.Equal(destination, GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal,
            Grant(destination.Id, GroupServiceCapability.Notify), [snapshot], [source.ExternalIdentity]).Destination);
    }

    [Fact]
    public void Destination_cannot_alias_any_customer_source_even_via_another_account()
    {
        var source = Source();
        var another = Source("other-customer");
        var destination = Destination() with { ExternalIdentity = another.ExternalIdentity with { AccountId = "different-account" } };
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal,
            Grant(destination.Id, GroupServiceCapability.Notify), [Snapshot(source, destination)], [source.ExternalIdentity, another.ExternalIdentity]));
        var sourceDestination = destination with { ExternalIdentity = source.ExternalIdentity };
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal,
            Grant(destination.Id, GroupServiceCapability.Notify), [Snapshot(source, sourceDestination)], []));
    }

    [Fact]
    public void Missing_route_duplicate_source_and_mixed_destination_have_no_fallback()
    {
        var source = Source();
        var destination = Destination();
        var snapshot = Snapshot(source, destination);
        var grant = Grant(destination.Id, GroupServiceCapability.Notify);
        Assert.Throws<InvalidOperationException>(() => GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal, grant, [], []));
        Assert.Throws<InvalidOperationException>(() => GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal, grant, [snapshot with { Route = null! }], []));
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal, grant, [snapshot, snapshot], []));
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal, grant,
            [snapshot, Snapshot(Source("second"), Destination())], []));
        Assert.Throws<UnauthorizedAccessException>(() => GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal,
            grant with { Capability = GroupServiceCapability.Ingest }, [snapshot], []));
    }

    [Fact]
    public void Authorization_copies_source_versions_and_exposes_an_immutable_list()
    {
        var source = Source();
        var destination = Destination();
        var snapshots = new[] { Snapshot(source, destination) };
        var result = GroupRoutingPolicy.AuthorizeNotification(Authentication, Principal,
            Grant(destination.Id, GroupServiceCapability.Notify), snapshots, []);
        snapshots[0] = Snapshot(Source("other"), destination);
        Assert.Equal(source.Scope.SourceBindingId, result.Routes[0].SourceBindingId);
        Assert.Throws<NotSupportedException>(() => ((IList<GroupRouteVersion>)result.Routes).Clear());
    }
}
