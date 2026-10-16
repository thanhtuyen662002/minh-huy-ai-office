using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public static class GroupIngressIdentity
{
    // Domain-separated fixed account values preserve the already reviewed
    // strict UTF8/length-prefix hash algorithm. None of these hashes is authority.
    public static string AccountIndex(string provider, string account) =>
        new GroupExternalIdentity(provider, account, "account-registry").IndexKey();
    public static string PhysicalGroupIndex(string provider, string group) =>
        new GroupExternalIdentity(provider, "physical-group-registry", group).IndexKey();
    public static string MessageIndex(GroupScope source, string message)
    {
        source.Validate();
        return new GroupExternalIdentity("source-message", source.SourceBindingId.ToString("D"), message).IndexKey();
    }
    public static string EventIndex(GroupScope source, string revisionEvent)
    {
        source.Validate();
        return new GroupExternalIdentity("source-revision", source.SourceBindingId.ToString("D"), revisionEvent).IndexKey();
    }

    internal static bool Matches(GroupBindingRecord binding, GroupExternalIdentity external) =>
        string.Equals(binding.Provider, external.Provider, StringComparison.Ordinal) &&
        string.Equals(binding.ExternalAccountId, external.AccountId, StringComparison.Ordinal) &&
        string.Equals(binding.ExternalGroupId, external.GroupId, StringComparison.Ordinal) &&
        string.Equals(binding.IdentityHash, external.IndexKey(), StringComparison.Ordinal) &&
        string.Equals(binding.PhysicalGroupHash, PhysicalGroupIndex(external.Provider, external.GroupId), StringComparison.Ordinal);
}
