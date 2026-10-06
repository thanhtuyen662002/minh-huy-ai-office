namespace Setup.Core;

public sealed record RuntimeStartPlan(bool Rebuild, bool Drain)
{
    public static RuntimeStartPlan Create(InstallProgress? previous, string revision, bool startOnly)
    {
        ProgressStore.ValidateRevision(revision);
        if (previous is not null && previous.SchemaVersion is not (1 or 2 or ProgressStore.CurrentSchemaVersion))
            throw new UnsupportedProgressException();
        var reuse = startOnly && previous is { SchemaVersion: ProgressStore.CurrentSchemaVersion, Phase: InstallPhase.Ready, ConfigurationRequired: true, InstallationId: not null }
            && previous.InstallationId != Guid.Empty
            && previous.Revision == revision;
        return new RuntimeStartPlan(Rebuild: !reuse, Drain: !reuse);
    }
}
