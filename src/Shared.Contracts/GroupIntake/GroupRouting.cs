using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

public sealed record GroupScope(Guid TenantId, Guid CompanyId, Guid SourceBindingId)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty || SourceBindingId == Guid.Empty)
            throw new InvalidOperationException("Group scope is incomplete.");
    }
}

/// <summary>External identities remain exact, including case and trailing whitespace.</summary>
public sealed record GroupExternalIdentity(string Provider, string AccountId, string GroupId)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public const int MaximumOpaqueIdLength = 256;

    public void Validate()
    {
        if (string.IsNullOrEmpty(Provider) || Provider.Length > 64 ||
            Provider.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-'))
            throw new InvalidOperationException("Connector provider is invalid.");
        ValidateOpaqueId(AccountId);
        ValidateOpaqueId(GroupId);
    }

    public static void ValidateOpaqueId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumOpaqueIdLength)
            throw new InvalidOperationException("External identity is invalid.");
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new InvalidOperationException("External identity is invalid."); }
    }

    /// <summary>Index key only. SQL readers must also verify full ordinal values.</summary>
    public string IndexKey()
    {
        Validate();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("aioffice-group-identity-v1\0"u8);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in new[] { Provider, AccountId, GroupId })
        {
            var bytes = StrictUtf8.GetBytes(value);
            BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public bool IsSameProviderGroup(GroupExternalIdentity other) =>
        string.Equals(Provider, other.Provider, StringComparison.Ordinal) &&
        string.Equals(GroupId, other.GroupId, StringComparison.Ordinal);
}

public enum GroupServiceCapability { Ingest = 1, Extract = 2, Notify = 3 }

public sealed record GroupServiceAuthentication(Guid ServiceId, long CredentialEpoch);
public sealed record GroupServicePrincipal(Guid TenantId, Guid CompanyId, Guid ServiceId, long CredentialEpoch, bool IsEnabled);
public sealed record GroupServiceGrant(Guid TenantId, Guid CompanyId, Guid ServiceId,
    Guid BindingId, GroupServiceCapability Capability, long Version, bool IsEnabled);

public sealed record SourceGroupBinding(GroupScope Scope, Guid ConnectorAccountId,
    GroupExternalIdentity ExternalIdentity, string DisplayName, long Version,
    long DeletionGeneration, bool IsEnabled);

public sealed record TechnicalDestinationBinding(Guid TenantId, Guid CompanyId, Guid Id,
    Guid ConnectorAccountId, GroupExternalIdentity ExternalIdentity, string DisplayName,
    long Version, bool IsEnabled);

public sealed record GroupNotificationRoute(GroupScope Source, Guid DestinationBindingId,
    long Version, long SourceBindingVersion, long DestinationBindingVersion, bool IsEnabled);

public sealed record GroupTechnicalAudienceGrant(GroupScope Source, Guid DestinationBindingId,
    long RouteVersion, long Version, bool IsEnabled);

public sealed record GroupRouteSnapshot(SourceGroupBinding Source,
    TechnicalDestinationBinding Destination, GroupNotificationRoute Route,
    GroupTechnicalAudienceGrant AudienceGrant);

public sealed record GroupRouteVersion(Guid SourceBindingId, long SourceVersion,
    long DeletionGeneration, long RouteVersion, long AudienceGrantVersion);

/// <summary>Constructed only by the routing policy from server-resolved snapshots.</summary>
public sealed class GroupNotificationAuthorization
{
    internal GroupNotificationAuthorization(TechnicalDestinationBinding destination,
        IReadOnlyList<GroupRouteVersion> routes)
    {
        Destination = destination;
        Routes = routes;
    }
    public TechnicalDestinationBinding Destination { get; }
    public IReadOnlyList<GroupRouteVersion> Routes { get; }
}

/// <summary>
/// Mechanical policy after trusted service authentication and fresh SQL resolution.
/// These methods do not authenticate caller-supplied snapshots or send messages.
/// </summary>
public static class GroupRoutingPolicy
{
    public const int MaximumReportSources = 32;

    public static GroupScope AuthorizeIngest(GroupServiceAuthentication authentication,
        GroupServicePrincipal principal, GroupServiceGrant grant, SourceGroupBinding source,
        GroupExternalIdentity observedIdentity, bool isGroup, bool isKnownReportEcho)
    {
        RequireSource(source);
        RequireService(authentication, principal, grant, source.Scope.TenantId,
            source.Scope.CompanyId, source.Scope.SourceBindingId, GroupServiceCapability.Ingest);
        observedIdentity.Validate();
        if (!isGroup || isKnownReportEcho || observedIdentity != source.ExternalIdentity)
            Deny();
        return source.Scope;
    }

    // An absent notification route cannot prevent safe extraction/attention notes.
    public static GroupScope AuthorizeExtraction(GroupServiceAuthentication authentication,
        GroupServicePrincipal principal, GroupServiceGrant grant, SourceGroupBinding source)
    {
        RequireSource(source);
        RequireService(authentication, principal, grant, source.Scope.TenantId,
            source.Scope.CompanyId, source.Scope.SourceBindingId, GroupServiceCapability.Extract);
        return source.Scope;
    }

    public static GroupNotificationAuthorization AuthorizeNotification(
        GroupServiceAuthentication authentication, GroupServicePrincipal principal,
        GroupServiceGrant notifyGrant, IReadOnlyList<GroupRouteSnapshot> snapshots,
        IReadOnlyList<GroupExternalIdentity> enrolledCustomerGroups)
    {
        if (snapshots is null || snapshots.Count is < 1 or > MaximumReportSources ||
            enrolledCustomerGroups is null)
            throw new InvalidOperationException("Notification snapshot is invalid.");
        var first = snapshots[0] ?? throw new InvalidOperationException("Notification snapshot is invalid.");
        var destination = first.Destination ?? throw new InvalidOperationException("Notification snapshot is invalid.");
        RequireDestination(destination);
        RequireService(authentication, principal, notifyGrant, destination.TenantId,
            destination.CompanyId, destination.Id, GroupServiceCapability.Notify);
        foreach (var customer in enrolledCustomerGroups)
        {
            if (customer is null) throw new InvalidOperationException("Customer identity is invalid.");
            customer.Validate();
            if (destination.ExternalIdentity.IsSameProviderGroup(customer)) Deny();
        }
        var seen = new HashSet<Guid>();
        var versions = new List<GroupRouteVersion>();
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null || snapshot.Source is null || snapshot.Route is null || snapshot.AudienceGrant is null)
                throw new InvalidOperationException("Notification snapshot is invalid.");
            var source = snapshot.Source;
            RequireSource(source);
            var route = snapshot.Route;
            var audience = snapshot.AudienceGrant;
            if (snapshot.Destination != destination || !seen.Add(source.Scope.SourceBindingId) ||
                source.Scope.TenantId != destination.TenantId || source.Scope.CompanyId != destination.CompanyId ||
                source.ExternalIdentity.IsSameProviderGroup(destination.ExternalIdentity) ||
                !route.IsEnabled || route.Version <= 0 || route.Source != source.Scope ||
                route.DestinationBindingId != destination.Id || route.SourceBindingVersion != source.Version ||
                route.DestinationBindingVersion != destination.Version || !audience.IsEnabled ||
                audience.Version <= 0 || audience.Source != source.Scope ||
                audience.DestinationBindingId != destination.Id || audience.RouteVersion != route.Version)
                Deny();
            versions.Add(new(source.Scope.SourceBindingId, source.Version,
                source.DeletionGeneration, route.Version, audience.Version));
        }
        return new(destination, Array.AsReadOnly(versions.OrderBy(x => x.SourceBindingId).ToArray()));
    }

    private static void RequireSource(SourceGroupBinding source)
    {
        if (source is null || source.Scope is null || source.ExternalIdentity is null)
            throw new InvalidOperationException("Source binding is invalid.");
        source.Scope.Validate();
        source.ExternalIdentity.Validate();
        if (source.ConnectorAccountId == Guid.Empty || source.Version <= 0 || source.DeletionGeneration < 0)
            throw new InvalidOperationException("Source binding is invalid.");
        if (!source.IsEnabled) Deny();
    }

    private static void RequireDestination(TechnicalDestinationBinding destination)
    {
        if (destination.TenantId == Guid.Empty || destination.CompanyId == Guid.Empty ||
            destination.Id == Guid.Empty || destination.ConnectorAccountId == Guid.Empty ||
            destination.Version <= 0 || destination.ExternalIdentity is null)
            throw new InvalidOperationException("Destination binding is invalid.");
        destination.ExternalIdentity.Validate();
        if (!destination.IsEnabled) Deny();
    }

    private static void RequireService(GroupServiceAuthentication authentication,
        GroupServicePrincipal principal, GroupServiceGrant grant, Guid tenantId, Guid companyId,
        Guid bindingId, GroupServiceCapability capability)
    {
        if (authentication is null || principal is null || grant is null)
            throw new InvalidOperationException("Service authority is incomplete.");
        if (authentication.ServiceId == Guid.Empty || principal.ServiceId == Guid.Empty ||
            authentication.CredentialEpoch <= 0 || principal.CredentialEpoch <= 0 || grant.Version <= 0)
            throw new InvalidOperationException("Service authority is invalid.");
        if (!principal.IsEnabled || !grant.IsEnabled ||
            authentication.ServiceId != principal.ServiceId ||
            authentication.CredentialEpoch != principal.CredentialEpoch ||
            principal.TenantId != tenantId || principal.CompanyId != companyId ||
            grant.TenantId != tenantId || grant.CompanyId != companyId ||
            grant.ServiceId != principal.ServiceId || grant.BindingId != bindingId || grant.Capability != capability)
            Deny();
    }

    private static void Deny() => throw new UnauthorizedAccessException("Group service operation is not authorized.");
}
