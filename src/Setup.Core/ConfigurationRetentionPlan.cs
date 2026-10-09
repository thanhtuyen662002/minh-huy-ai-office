namespace Setup.Core;

public sealed record ConfigurationRetentionPlan(bool RequireExistingInstallation, Guid? InstallationId)
{
    public static ConfigurationRetentionPlan Create(InstallProgress? previous, bool retainedProgress, bool existingConfiguration)
    {
        if (previous is not null && previous.SchemaVersion is not (1 or 2 or ProgressStore.CurrentSchemaVersion))
            throw new UnsupportedProgressException();
        // Legacy or damaged progress cannot prove that no installation existed.
        // Persist that uncertainty before prerequisites or a reboot can erase it.
        var required = retainedProgress || existingConfiguration || (previous is not null
            && (previous.SchemaVersion < ProgressStore.CurrentSchemaVersion || previous.ConfigurationRequired || previous.InstallationId is not null));
        return new(required, previous?.InstallationId);
    }
}
