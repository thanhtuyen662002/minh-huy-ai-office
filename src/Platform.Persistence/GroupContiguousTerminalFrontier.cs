using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Metadata planning only. The future owned SQL caller must authenticate every
// immutable terminal manifest and its full original-effect graph before supplying
// a terminal interval. A shaped digest is not completion or write authority.
internal static class GroupContiguousTerminalFrontier
{
    internal const int MaximumIntervals = 100;
    internal sealed record Allocation(GroupScope Scope, Guid BatchId, long AfterSequence, long ThroughSequence, int RawCount);
    internal sealed record Terminal(GroupScope Scope, Guid BatchId, Guid OperationId, int ManifestVersion,
        string ManifestSha256, long AfterSequence, long ThroughSequence);
    internal sealed record Plan(long ThroughSequence, int AdvancedIntervals, bool HasScheduledBacklog,
        bool HasUnscheduledBacklog, bool HasCoverageGap, bool NeedsContinuation)
    {
        public override string ToString() => "Group contiguous frontier plan (metadata prerequisite).";
    }

    internal static Plan Require(GroupScope scope, long currentThrough, long scheduledThrough, long committedThrough,
        bool hasCoverageGap, IEnumerable<Allocation> allocationRows, IEnumerable<Terminal> terminalRows,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (scope.TenantId == Guid.Empty || scope.CompanyId == Guid.Empty || scope.SourceBindingId == Guid.Empty
            || currentThrough < 0 || scheduledThrough < currentThrough || committedThrough < scheduledThrough) throw Unavailable();
        // Freeze both actual enumerations before checking anything that could
        // accidentally trust a collection's Count, indexer or CopyTo method.
        var allocations = Freeze(allocationRows, token).OrderBy(x => x.AfterSequence).ToArray();
        var terminals = Freeze(terminalRows, token);
        var ids = new HashSet<Guid>(); var after = currentThrough;
        foreach (var row in allocations)
        {
            if (row.Scope != scope || row.BatchId == Guid.Empty || !ids.Add(row.BatchId) || row.AfterSequence != after
                || row.ThroughSequence <= row.AfterSequence || row.ThroughSequence > scheduledThrough
                || row.RawCount is < 1 or > GroupBatchAllocationPrefix.MaximumRawRevisions
                || row.ThroughSequence - row.AfterSequence != row.RawCount) throw Unavailable();
            after = row.ThroughSequence;
        }
        // A short result must contain every scheduled interval. A full bounded
        // window may continue later; it never implies the unqueried suffix is done.
        if (after < scheduledThrough && allocations.Length != MaximumIntervals) throw Unavailable();
        var byBatch = allocations.ToDictionary(x => x.BatchId);
        var complete = new HashSet<Guid>(); var operations = new HashSet<Guid>();
        foreach (var row in terminals)
        {
            if (row.Scope != scope || row.BatchId == Guid.Empty || row.OperationId == Guid.Empty
                || row.ManifestVersion != 1 || row.ManifestSha256 is not { Length: 64 }
                || row.ManifestSha256.Any(x => x is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
                || row.ManifestSha256.All(x => x == '0') || !byBatch.TryGetValue(row.BatchId, out var original)
                || row.AfterSequence != original.AfterSequence || row.ThroughSequence != original.ThroughSequence
                || !complete.Add(row.BatchId) || !operations.Add(row.OperationId)) throw Unavailable();
        }
        var through = currentThrough; var advanced = 0;
        foreach (var row in allocations)
        {
            if (!complete.Contains(row.BatchId)) break;
            through = row.ThroughSequence; advanced++;
        }
        token.ThrowIfCancellationRequested();
        return new(through, advanced, through < scheduledThrough, scheduledThrough < committedThrough, hasCoverageGap,
            through == after && after < scheduledThrough);
    }

    private static T[] Freeze<T>(IEnumerable<T> source, CancellationToken token) where T : class
    {
        if (source is null) throw Unavailable();
        var rows = new List<T>(MaximumIntervals);
        try
        {
            foreach (var row in source)
            {
                token.ThrowIfCancellationRequested();
                if (row is null || rows.Count == MaximumIntervals) throw Unavailable();
                rows.Add(row);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Unavailable(); }
        return rows.ToArray();
    }
    private static InvalidOperationException Unavailable() => new("Contiguous group terminal frontier is not available.");
}
