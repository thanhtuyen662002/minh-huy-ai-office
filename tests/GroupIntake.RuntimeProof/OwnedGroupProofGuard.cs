namespace MinhHuy.AIOffice.GroupIntake.RuntimeProof;

public static class OwnedGroupProofGuard
{
    // Before any file, secret, network, process or SQL access. This harness is
    // not shipped in application containers and cannot run on customer config.
    public static string RequireOwned(Func<string, string?> environment)
    {
        var temp = environment("RUNNER_TEMP");
        if (environment("CI") != "true" || environment("GITHUB_ACTIONS") != "true" ||
            environment("AIOFFICE_OWNED_GROUP_SPOOL_PROOF") != "true" || string.IsNullOrEmpty(temp) ||
            !Path.IsPathFullyQualified(temp) || Path.GetFullPath(temp) == Path.GetPathRoot(Path.GetFullPath(temp)))
            throw new InvalidOperationException("Owned group spool proof is unavailable.");
        return Path.Combine(Path.GetFullPath(temp), "aioffice-local", "group-spool-proof");
    }
}
