using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Fixed trusted worker configuration, never a broker payload or portal DTO.
public sealed record GroupExtractionWorkerBinding(Guid TenantId, Guid CompanyId, Guid ServiceId, long CredentialEpoch)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty || ServiceId == Guid.Empty || CredentialEpoch <= 0)
            throw new InvalidOperationException("Group worker binding is not available.");
    }
}

internal sealed record GroupExtractionAuthority(GroupServicePrincipal Principal,
    GroupServiceGrant Grant, SourceGroupBinding Source, long AccountVersion, string CredentialReference);

internal sealed class GroupExtractionDirectory(PlatformDbContext database, GroupExtractionWorkerBinding worker)
{
    internal async Task<GroupExtractionAuthority> RequireAsync(GroupScope scope, CancellationToken cancellationToken)
    {
        worker.Validate(); scope.Validate();
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        var hash = await database.GroupBindings.AsNoTracking().Where(x => x.TenantId == scope.TenantId &&
            x.CompanyId == scope.CompanyId && x.Id == scope.SourceBindingId).Select(x => x.IdentityHash).SingleOrDefaultAsync(cancellationToken);
        if (hash is null || hash.Length != 64 || hash.Any(c => !"0123456789ABCDEF".Contains(c)))
            throw GroupServiceDirectory.Denied();
        var binding = await GroupRegistryReader.BindingAsync(database, hash, cancellationToken);
        if (binding is null || binding.TenantId != scope.TenantId || binding.CompanyId != scope.CompanyId || binding.Id != scope.SourceBindingId ||
            binding.Role != GroupBindingRole.CustomerSource || !binding.IsEnabled || binding.Version <= 0 || binding.DeletionGeneration < 0)
            throw GroupServiceDirectory.Denied();
        var identity = new GroupExternalIdentity(binding.Provider, binding.ExternalAccountId, binding.ExternalGroupId);
        if (!GroupIngressIdentity.Matches(binding, identity) || !await GroupRegistryReader.IsExclusivePhysicalOwnerAsync(database, binding, cancellationToken))
            throw GroupServiceDirectory.Denied();
        if (!await database.Companies.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId && x.Id == scope.CompanyId && x.IsActive, cancellationToken))
            throw GroupServiceDirectory.Denied();
        var account = await GroupRegistryReader.AccountAsync(database, scope.TenantId, scope.CompanyId, binding.ConnectorAccountId, cancellationToken);
        if (account is null || !account.IsEnabled || account.Version <= 0 || account.Provider != identity.Provider ||
            account.ExternalAccountId != identity.AccountId || account.IdentityHash != GroupIngressIdentity.AccountIndex(identity.Provider, identity.AccountId))
            throw GroupServiceDirectory.Denied();
        var service = await database.GroupServices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId &&
            x.CompanyId == scope.CompanyId && x.Id == worker.ServiceId, cancellationToken);
        if (service is null || !service.IsEnabled || service.CredentialEpoch != worker.CredentialEpoch ||
            !SecretReference.TryParse(service.CredentialReference, out var reference) || reference!.Value != service.CredentialReference)
            throw GroupServiceDirectory.Denied();
        var grant = await database.GroupServiceGrants.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId &&
            x.CompanyId == scope.CompanyId && x.ServiceId == worker.ServiceId && x.BindingId == scope.SourceBindingId &&
            x.Capability == GroupServiceCapability.Extract, cancellationToken) ?? throw GroupServiceDirectory.Denied();
        var source = new SourceGroupBinding(scope, binding.ConnectorAccountId, identity, binding.DisplayName,
            binding.Version, binding.DeletionGeneration, binding.IsEnabled);
        var principal = new GroupServicePrincipal(scope.TenantId, scope.CompanyId, service.Id, service.CredentialEpoch, service.IsEnabled);
        var capability = new GroupServiceGrant(scope.TenantId, scope.CompanyId, service.Id, binding.Id,
            grant.Capability, grant.Version, grant.IsEnabled);
        GroupRoutingPolicy.AuthorizeExtraction(new(worker.ServiceId, worker.CredentialEpoch), principal, capability, source);
        return new(principal, capability, source, account.Version, service.CredentialReference);
    }

    internal async Task RequireCurrentAsync(GroupExtractionAuthority initial, CancellationToken cancellationToken)
    {
        if (await RequireAsync(initial.Source.Scope, cancellationToken) != initial) throw GroupServiceDirectory.Denied();
    }
}
