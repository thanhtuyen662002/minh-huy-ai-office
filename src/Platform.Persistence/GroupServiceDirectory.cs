using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed record GroupIngestAuthority(GroupServicePrincipal Principal,
    GroupServiceGrant Grant, SourceGroupBinding Source, GroupConnectorAccountRecord Account,
    string CredentialReference);

// Only server-authenticated callers may enter the persistence pipeline. A portal
// JWT, customer participant or model proposal cannot construct this identity.
public sealed class AuthenticatedGroupService
{
    internal AuthenticatedGroupService(GroupIngestAuthority authority, DateTimeOffset signedAtUtc)
    {
        ServiceId = authority.Principal.ServiceId; CredentialEpoch = authority.Principal.CredentialEpoch;
        Source = authority.Source.Scope; External = authority.Source.ExternalIdentity;
        SourceVersion = authority.Source.Version; DeletionGeneration = authority.Source.DeletionGeneration;
        AccountVersion = authority.Account.Version; GrantVersion = authority.Grant.Version;
        ConnectorAccountId = authority.Account.Id;
        CredentialReference = authority.CredentialReference;
        SignedAtUtc = signedAtUtc;
    }
    public Guid ServiceId { get; }
    public long CredentialEpoch { get; }
    public GroupScope Source { get; }
    public GroupExternalIdentity External { get; }
    internal long SourceVersion { get; }
    internal long DeletionGeneration { get; }
    internal long AccountVersion { get; }
    internal Guid ConnectorAccountId { get; }
    internal long GrantVersion { get; }
    internal string CredentialReference { get; }
    internal DateTimeOffset SignedAtUtc { get; }
}

internal sealed class GroupServiceDirectory(PlatformDbContext database)
{
    internal async Task<GroupIngestAuthority> RequireIngestAsync(GroupServiceAuthentication authentication,
        GroupExternalIdentity external, CancellationToken cancellationToken)
    {
        external.Validate();
        if (authentication.ServiceId == Guid.Empty || authentication.CredentialEpoch <= 0) throw Denied();
        var index = external.IndexKey();
        var binding = await GroupRegistryReader.BindingAsync(database, index, cancellationToken);
        if (binding is null || !GroupIngressIdentity.Matches(binding, external) ||
            binding.Role != GroupBindingRole.CustomerSource || !binding.IsEnabled ||
            binding.Version <= 0 || binding.DeletionGeneration < 0) throw Denied();
        if (!await GroupRegistryReader.IsExclusivePhysicalOwnerAsync(database, binding, cancellationToken)) throw Denied();
        var company = await database.Companies.AsNoTracking().AnyAsync(x => x.TenantId == binding.TenantId &&
            x.Id == binding.CompanyId && x.IsActive, cancellationToken);
        if (!company) throw Denied();
        var account = await GroupRegistryReader.AccountAsync(database, binding.TenantId, binding.CompanyId, binding.ConnectorAccountId, cancellationToken);
        if (account is null || !account.IsEnabled || account.Version <= 0 ||
            !string.Equals(account.Provider, external.Provider, StringComparison.Ordinal) ||
            !string.Equals(account.ExternalAccountId, external.AccountId, StringComparison.Ordinal) ||
            account.IdentityHash != GroupIngressIdentity.AccountIndex(external.Provider, external.AccountId)) throw Denied();
        var service = await database.GroupServices.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId && x.Id == authentication.ServiceId, cancellationToken);
        if (service is null || !service.IsEnabled || service.CredentialEpoch != authentication.CredentialEpoch ||
            !SecretReference.TryParse(service.CredentialReference, out var reference) ||
            !string.Equals(reference!.Value, service.CredentialReference, StringComparison.Ordinal)) throw Denied();
        var grant = await database.GroupServiceGrants.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId && x.ServiceId == service.Id &&
            x.BindingId == binding.Id && x.Capability == GroupServiceCapability.Ingest, cancellationToken);
        if (grant is null) throw Denied();
        var source = new SourceGroupBinding(new(binding.TenantId, binding.CompanyId, binding.Id), binding.ConnectorAccountId,
            external, binding.DisplayName, binding.Version, binding.DeletionGeneration, binding.IsEnabled);
        var principal = new GroupServicePrincipal(service.TenantId, service.CompanyId, service.Id, service.CredentialEpoch, service.IsEnabled);
        var capability = new GroupServiceGrant(grant.TenantId, grant.CompanyId, grant.ServiceId, grant.BindingId,
            grant.Capability, grant.Version, grant.IsEnabled);
        GroupRoutingPolicy.AuthorizeIngest(authentication, principal, capability, source, external, isGroup: true, isKnownReportEcho: false);
        return new(principal, capability, source, account, service.CredentialReference);
    }

    internal async Task<GroupIngestAuthority> RequireCurrentAsync(AuthenticatedGroupService service, CancellationToken cancellationToken)
    {
        var current = await RequireIngestAsync(new(service.ServiceId, service.CredentialEpoch), service.External, cancellationToken);
        if (current.Source.Scope != service.Source || current.Source.Version != service.SourceVersion ||
            current.Source.DeletionGeneration != service.DeletionGeneration || current.Account.Id != service.ConnectorAccountId || current.Account.Version != service.AccountVersion ||
            current.Grant.Version != service.GrantVersion || current.CredentialReference != service.CredentialReference) throw Denied();
        return current;
    }

    internal static UnauthorizedAccessException Denied() => new("Group service access is not available.");
}
