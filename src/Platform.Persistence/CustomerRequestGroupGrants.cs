using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Source groups a service may file and read tickets for: the configured allowlist plus, when the service opts in,
// the customer groups an administrator selected in Kênh & Bot for the same tenant/company.
internal static class CustomerRequestGroupGrants
{
    internal static async Task<string[]> GroupsAsync(PlatformDbContext database, CustomerRequestServiceBinding binding, CancellationToken ct)
    {
        if (!binding.GrantBotChannelGroups) return binding.AllowedSourceGroupExternalIds.ToArray();
        var selected = await database.BotCustomerGroups.AsNoTracking()
            .Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId).Select(x => x.ExternalId).ToArrayAsync(ct);
        return binding.AllowedSourceGroupExternalIds.Union(selected, StringComparer.Ordinal).ToArray();
    }

    // Called inside the locked company transaction, after the signature is verified.
    internal static async Task RequireAsync(PlatformDbContext database, CustomerRequestServiceBinding binding, string group, CancellationToken ct)
    {
        if (binding.AllowedSourceGroupExternalIds.Contains(group, StringComparer.Ordinal)) return;
        if (!binding.GrantBotChannelGroups) throw CustomerRequestServiceDirectory.Denied();
        var matches = await database.BotCustomerGroups.AsNoTracking()
            .Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId && x.ExternalId == group).Select(x => x.ExternalId).ToArrayAsync(ct);
        if (!matches.Contains(group, StringComparer.Ordinal)) throw CustomerRequestServiceDirectory.Denied();
    }
}
