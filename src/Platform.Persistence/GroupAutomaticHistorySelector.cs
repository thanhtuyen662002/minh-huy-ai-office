using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Host-selected metadata, never model-selected SQL or evidence. The existing
// BrainReader must freshly validate/decrypt these IDs, and the original input
// store must persist that exact selection before any provider attempt.
internal sealed class GroupAutomaticHistorySelection
{
    internal GroupAutomaticHistorySelection(GroupScope scope, Guid[] requests, Guid[] glossary, bool limited)
    {
        Scope = scope; RequestIds = Array.AsReadOnly(requests); GlossaryIds = Array.AsReadOnly(glossary);
        HasAdditionalHistory = limited;
    }
    internal GroupScope Scope { get; }
    internal IReadOnlyList<Guid> RequestIds { get; }
    internal IReadOnlyList<Guid> GlossaryIds { get; }
    internal bool HasAdditionalHistory { get; }
    public override string ToString() => "Group history selection (private metadata).";
}

internal sealed class GroupAutomaticHistorySelector(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources)
{
    private const int ProbeLimit = GroupBrainCurrentReader.MaximumSelectedRevisions + 1;
    private const int GlossaryLimit = 4;

    internal async Task<GroupAutomaticHistorySelection> SelectAsync(GroupBatchSourcePreparation preparation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparation); token.ThrowIfCancellationRequested();
        var handle = preparation.Context.Handle; var scope = preparation.Scope;
        worker.Validate(); scope.Validate();
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch)
            throw GroupServiceDirectory.Denied();
        if (sources is null || !sources.UsesContext(database, worker, clock)
            || database.ChangeTracker.HasChanges() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.Database.IsRelational() && !database.Database.IsSqlServer()) throw Unavailable();
        if (database.Database.IsSqlServer())
        {
            try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); token = deadline.Token;
        var hints = GroupAutomaticHistoryHints.Create(preparation, token);
        await sources.RequireCurrentAsync(preparation.Context, token);
        try
        {
            GroupAutomaticHistorySelection selection;
            await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token))
            {
                var claims = new GroupBatchClaimStore(database, worker, clock);
                var permissions = new GroupWorkNotePermissionVerifier(database);
                await FenceAsync();
                var codeRows = hints.RequestCodes.Count == 0 ? [] : await QueryRequests(preparation, hints, 0).ToArrayAsync(token);
                var replyRows = hints.ReplyMessageIndexes.Count == 0 ? [] : await QueryRequests(preparation, hints, 1).ToArrayAsync(token);
                var recentRows = await QueryRequests(preparation, hints, 2).ToArrayAsync(token);
                var glossaryRows = await QueryGlossary(preparation).ToArrayAsync(token);
                selection = Rank(scope, codeRows, replyRows, recentRows, glossaryRows, hints.HasAdditionalRequestCodes, token);
                await FenceAsync();
                if (database.ChangeTracker.HasChanges()) throw Unavailable();
                await transaction.CommitAsync(token);

                async Task FenceAsync()
                {
                    await permissions.RequireSafeRuntimeAsync(token);
                    var verdict = await claims.InspectCurrentLockedAsync(handle, token);
                    if (verdict is GroupBatchClaimFenceVerdict.Current) return;
                    await claims.RetireExpiredLockedAsync(((GroupBatchClaimFenceVerdict.Expired)verdict).Observation, token);
                    await transaction.CommitAsync(token);
                    throw GroupServiceDirectory.Denied();
                }
            }
            // An edit/recall/expiry while metadata was selected must prevent
            // release. Selection never falls back to empty history on failure.
            await sources.RequireCurrentAsync(preparation.Context, token);
            token.ThrowIfCancellationRequested(); return selection;
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException) { throw Unavailable(); }
    }

    // These fixed internal query factories release no plaintext or authority.
    // Normal consumption is exclusively inside the fenced unit above. They
    // also allow checking actual SQL translation offline without a database.
    internal IQueryable<Candidate> QueryRequests(GroupBatchSourcePreparation preparation, GroupAutomaticHistoryHints hints, int kind)
    {
        var scope = preparation.Scope; var handle = preparation.Context.Handle;
        if (hints.Scope != scope || kind is < 0 or > 2) throw Unavailable();
        var rows = database.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.SourceVersion == handle.Authority.Source.Version
            && x.DeletionGeneration == handle.Authority.Source.DeletionGeneration
            && x.OriginBatchId != preparation.BatchId);
        var codes = hints.RequestCodes.ToArray(); var indexes = hints.ReplyMessageIndexes.ToArray();
        rows = kind switch
        {
            0 => rows.Where(x => codes.Contains(x.RequestCode)),
            1 => rows.Where(request => database.GroupRequestEvidence.Any(e => e.TenantId == scope.TenantId && e.CompanyId == scope.CompanyId
                && e.BindingId == scope.SourceBindingId && e.RequestId == request.Id && e.RequestRevision == request.CurrentRevision
                && e.Kind == GroupRequestEvidenceKind.LiteralSourceQuote
                && database.GroupMessages.Any(m => m.TenantId == scope.TenantId && m.CompanyId == scope.CompanyId
                    && m.BindingId == scope.SourceBindingId && m.Id == e.MessageId && indexes.Contains(m.IdentityHash)))),
            _ => rows.Where(x => x.BusinessStatus == GroupNoteBusinessStatus.New || x.BusinessStatus == GroupNoteBusinessStatus.NeedsClarification)
        };
        return rows.OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new Candidate(x.Id, x.CurrentRevision, x.UpdatedAtUtc)).Take(ProbeLimit);
    }

    internal IQueryable<Candidate> QueryGlossary(GroupBatchSourcePreparation preparation)
    {
        var scope = preparation.Scope; var handle = preparation.Context.Handle;
        return database.GroupGlossaryEntries.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.SourceVersion == handle.Authority.Source.Version && x.DeletionGeneration == handle.Authority.Source.DeletionGeneration
            && x.IsEnabled && x.AllowExtraction).OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new Candidate(x.Id, x.CurrentRevision, x.CreatedAtUtc)).Take(GlossaryLimit + 1);
    }

    internal sealed record Candidate(Guid Id, long Revision, DateTimeOffset AtUtc);

    // Bounded executable ranking also supports cold negative/limit controls.
    // Exact code matches outrank replies, then recent open work, then glossary.
    // A reply can refer to several requests; selecting them grants no merge or
    // update authority. Later linking must resolve ambiguity from current facts.
    internal static GroupAutomaticHistorySelection Rank(GroupScope scope, Candidate[] codes, Candidate[] replies,
        Candidate[] recent, Candidate[] glossary, bool hintsLimited, CancellationToken token = default)
    {
        scope.Validate(); token.ThrowIfCancellationRequested();
        var requests = new List<Guid>(); var entries = new List<Guid>(); var seen = new HashSet<Guid>();
        var limited = hintsLimited;
        foreach (var group in new[] { codes, replies, recent })
        {
            Validate(group, ProbeLimit);
            if (group.Length == ProbeLimit) limited = true;
            foreach (var row in group)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(row.Id)) continue;
                if (requests.Count == GroupBrainCurrentReader.MaximumSelectedRevisions) { limited = true; continue; }
                requests.Add(row.Id);
            }
        }
        Validate(glossary, GlossaryLimit + 1);
        var capacity = Math.Min(GlossaryLimit, GroupBrainCurrentReader.MaximumSelectedRevisions - requests.Count);
        limited |= glossary.Length > capacity;
        entries.AddRange(glossary.Take(capacity).Select(x => x.Id));
        return new(scope, requests.ToArray(), entries.ToArray(), limited);

        static void Validate(Candidate[] values, int maximum)
        {
            if (values is null || values.Length > maximum || values.Select(x => x.Id).Distinct().Count() != values.Length
                || values.Any(x => x.Id == Guid.Empty || x.Revision <= 0 || x.AtUtc.Offset != TimeSpan.Zero)) throw Unavailable();
        }
    }
    private static InvalidOperationException Unavailable() => new("Group history selection is not available.");
}
