using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Private exact dependencies, never provider context or permission to complete
// a gap. The existing boolean remains a conservative observation for consumers.
internal sealed class GroupBatchCoverageSnapshot
{
    internal const int MaximumRecordsPerKind = 256;
    private readonly GroupSourceCoverageGapSnapshot[] source;
    private readonly GroupAccountCoverageGapSnapshot[] account;

    private GroupBatchCoverageSnapshot(GroupSourceCoverageGapSnapshot[] source, GroupAccountCoverageGapSnapshot[] account)
    { this.source = source; this.account = account; }

    internal bool HasGaps => source.Length != 0 || account.Length != 0;
    internal bool Same(GroupBatchCoverageSnapshot other) => source.SequenceEqual(other.source) && account.SequenceEqual(other.account);
    internal string Fingerprint() => GroupWorkDependencyManifest.Fingerprint("aioffice-group-coverage-dependency-v1", new { source, account });
    public override string ToString() => "Group coverage dependencies (private metadata).";

    // Called only within the source reader's fenced serializable SQL unit.
    // Both kinds are read even when the first contains gaps; no short circuit
    // may hide a later account interruption. A validated overflow sentinel
    // refuses instead of silently presenting a partial dependency set.
    internal static async Task<GroupBatchCoverageSnapshot> ReadAsync(PlatformDbContext database, GroupScope scope,
        Guid accountId, CancellationToken token)
    {
        var sources = await database.GroupCoverageGaps.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.Id)
            .Select(x => new GroupSourceCoverageGapSnapshot(x.Id, x.AfterCommittedSequence, x.Reason, x.OpenedAtUtc, x.ReconnectedAtUtc))
            .Take(MaximumRecordsPerKind + 1).ToArrayAsync(token);
        if (sources.Length > MaximumRecordsPerKind || sources.Any(x => x.Id == Guid.Empty || x.AfterCommittedSequence < 0
            || !Reason(x.Reason) || !Utc(x.OpenedAtUtc) || x.ReconnectedAtUtc is { } reconnected && (!Utc(reconnected) || reconnected < x.OpenedAtUtc))
            || sources.Select(x => x.Id).Distinct().Count() != sources.Length) throw Unavailable();
        var accounts = await database.GroupAccountCoverageGaps.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.ConnectorAccountId == accountId).OrderBy(x => x.ListenerEpoch).ThenBy(x => x.Reason)
            .Select(x => new GroupAccountCoverageGapSnapshot(x.ListenerEpoch, x.Reason, x.OpenedAtUtc, x.RecordedAtUtc))
            .Take(MaximumRecordsPerKind + 1).ToArrayAsync(token);
        if (accounts.Length > MaximumRecordsPerKind || accounts.Any(x => x.ListenerEpoch <= 0
            || x.Reason is not ("listener-started" or "listener-expired" or "listener-stopped")
            || !Utc(x.OpenedAtUtc) || !Utc(x.RecordedAtUtc) || x.RecordedAtUtc < x.OpenedAtUtc)
            || accounts.Select(x => (x.ListenerEpoch, x.Reason)).Distinct().Count() != accounts.Length) throw Unavailable();
        return new(sources, accounts);
    }

    private static bool Reason(string? value) => value is { Length: >= 1 and <= 64 } && value.All(c => c is >= ' ' and <= '~');
    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;
    private static InvalidOperationException Unavailable() => new("Group coverage dependencies are not available.");
}

internal sealed record GroupSourceCoverageGapSnapshot(Guid Id, long AfterCommittedSequence, string Reason,
    DateTimeOffset OpenedAtUtc, DateTimeOffset? ReconnectedAtUtc);
internal sealed record GroupAccountCoverageGapSnapshot(long ListenerEpoch, string Reason,
    DateTimeOffset OpenedAtUtc, DateTimeOffset RecordedAtUtc);
