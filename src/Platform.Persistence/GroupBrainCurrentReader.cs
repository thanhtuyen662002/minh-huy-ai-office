using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Existing AI/glossary revisions remain opaque here. Host attention additionally
// requires its closed metadata payload to match exact protected evidence.
// A future qualified context must quarantine and budget all content before release.
// This reader neither invokes a model nor grants future provider/effect authority.
public sealed class GroupBrainPrivateRevision
{
    internal GroupBrainPrivateRevision(GroupBrainSnapshot snapshot, string content)
    {
        Kind = snapshot.Kind; RecordId = snapshot.RecordId; Revision = snapshot.Revision.Revision;
        RequestCode = snapshot.Request?.RequestCode; BusinessStatus = snapshot.Request?.BusinessStatus;
        Origin = snapshot.Request is null ? null : snapshot.Revision.Origin;
        VerificationLevel = snapshot.Request is null ? null : snapshot.Revision.VerificationLevel;
        IsItConfirmed = snapshot.Request?.ConfirmedByUserId is not null; Content = content;
    }
    public GroupBrainContentKind Kind { get; }
    public Guid RecordId { get; }
    public long Revision { get; }
    public string? RequestCode { get; }
    public GroupNoteBusinessStatus? BusinessStatus { get; }
    public GroupRequestRevisionOrigin? Origin { get; }
    public GroupRequestVerificationLevel? VerificationLevel { get; }
    public bool IsItConfirmed { get; }
    public string Content { get; }
    public override string ToString() => "Group brain revision (private content).";
}

public sealed class GroupBrainPrivateContext
{
    internal GroupBrainPrivateContext(GroupBatchClaimHandle handle, GroupBrainSnapshot[] snapshots,
        GroupBrainPrivateRevision[] items)
    { Handle = handle; Snapshots = Array.AsReadOnly(snapshots); Items = Array.AsReadOnly(items); }
    internal GroupBatchClaimHandle Handle { get; }
    internal IReadOnlyList<GroupBrainSnapshot> Snapshots { get; }
    public GroupScope Scope => Handle.Receipt.Scope;
    public IReadOnlyList<GroupBrainPrivateRevision> Items { get; }
    public override string ToString() => "Group brain context (private content).";
}

// Explicit host-selected revision IDs; no model-selectable SQL or portal grant.
// Each read owns its transaction. Even an empty selection fences current Extract.
public sealed class GroupBrainCurrentReader(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, IGroupSourceKeyProvider keys, GroupBrainContentProtector protector)
{
    public const int MaximumSelectedRevisions = 20;
    public const int MaximumSelectedEnvelopeBytes = 256000;

    public async Task<GroupBrainPrivateContext> ReadAsync(GroupBatchClaimHandle handle,
        IReadOnlyList<Guid> requestIds, IReadOnlyList<Guid> glossaryIds, CancellationToken cancellationToken = default)
    {
        ValidateEntry(handle, cancellationToken);
        var selected = Select(requestIds, glossaryIds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        try
        {
            var initial = await ReadUnitAsync(handle, selected, null, cancellationToken);
            var content = new Dictionary<(GroupBrainContentKind, Guid), string>();
            foreach (var group in initial.GroupBy(x => x.Revision.ContentKeyId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var key = await ResolveKeyAsync(handle.Receipt.Scope, group.Key, cancellationToken);
                if (key is null || key.KeyId != group.Key) throw Unavailable();
                // Resolve outside SQL, then recheck every exact contributing
                // head/registry/evidence/claim before decrypting any revision.
                await ReadUnitAsync(handle, selected, initial, cancellationToken);
                foreach (var snapshot in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var revision = snapshot.Revision;
                    var value = protector.Unprotect(new(handle.Receipt.Scope, snapshot.Kind, snapshot.RecordId,
                        revision.Revision, revision.SourceVersion, revision.DeletionGeneration),
                        revision.ProtectedContent!, key.Key, key.KeyId);
                    ValidateHostPayload(snapshot, value);
                    content.Add((snapshot.Kind, snapshot.RecordId), value);
                }
            }
            await ReadUnitAsync(handle, selected, initial, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(handle, initial, initial.Select(x => new GroupBrainPrivateRevision(x, content[(x.Kind, x.RecordId)])).ToArray());
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException)
        { throw Unavailable(); }
    }

    // An instant exact-dependency fence. Later release/effect units must own
    // their own final checks and (for effects) staged rollback/expiry retirement.
    public async Task RequireCurrentAsync(GroupBrainPrivateContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); ValidateEntry(context.Handle, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var selected = context.Snapshots.Select(x => (x.Kind, x.RecordId)).ToArray();
        try { await ReadUnitAsync(context.Handle, selected, context.Snapshots, deadline.Token); }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException) { throw Unavailable(); }
    }

    private async Task<GroupBrainSnapshot[]> ReadUnitAsync(GroupBatchClaimHandle handle,
        (GroupBrainContentKind Kind, Guid Id)[] selected, IReadOnlyList<GroupBrainSnapshot>? expected, CancellationToken token)
    {
        ValidateEntry(handle, token);
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token);
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var permissions = new GroupWorkNotePermissionVerifier(database);
        await FenceAsync();
        var snapshots = await ReadLockedAsync(handle, selected, expected, token);
        await FenceAsync();
        if (database.ChangeTracker.HasChanges()) throw Unavailable();
        await transaction.CommitAsync(token);
        return snapshots.ToArray();

        async Task FenceAsync()
        {
            await permissions.RequireSafeRuntimeAsync(token);
            var verdict = await claims.InspectCurrentLockedAsync(handle, token);
            if (verdict is GroupBatchClaimFenceVerdict.Current) return;
            // This fixed unit contains only reads. No staged effects are
            // committed with the durable metadata-only expiry witness.
            await claims.RetireExpiredLockedAsync(((GroupBatchClaimFenceVerdict.Expired)verdict).Observation, token);
            await transaction.CommitAsync(token);
            throw GroupServiceDirectory.Denied();
        }
    }

    // Fixed metadata/cipher dependency check inside an owned SQL effect unit.
    // No keys/decryption, independent transaction, expiry write or commit.
    internal async Task<GroupBatchClaimFenceVerdict> RequireUnchangedLockedAsync(GroupBrainPrivateContext context,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateLockedEntry(context.Handle, token);
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var permissions = new GroupWorkNotePermissionVerifier(database);
        await permissions.RequireSafeRuntimeAsync(token);
        var verdict = await claims.InspectCurrentLockedAsync(context.Handle, token);
        if (verdict is GroupBatchClaimFenceVerdict.Expired) return verdict;
        await ReadLockedAsync(context.Handle, context.Snapshots.Select(x => (x.Kind, x.RecordId)).ToArray(), context.Snapshots, token);
        await permissions.RequireSafeRuntimeAsync(token);
        return await claims.InspectCurrentLockedAsync(context.Handle, token);
    }

    private void ValidateLockedEntry(GroupBatchClaimHandle handle, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested(); worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        if (!database.Database.IsSqlServer() || database.Database.CurrentTransaction is not { } transaction
            || !transaction.SupportsSavepoints || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable
            || database.ChangeTracker.HasChanges()) throw Unavailable();
    }

    private async Task<GroupBrainSnapshot[]> ReadLockedAsync(GroupBatchClaimHandle handle,
        (GroupBrainContentKind Kind, Guid Id)[] selected, IReadOnlyList<GroupBrainSnapshot>? expected, CancellationToken token)
    {
        var snapshots = new List<GroupBrainSnapshot>();
        foreach (var (kind, id) in selected)
        {
            var snapshot = kind == GroupBrainContentKind.RequestRevision
                ? await RequestAsync(handle, id, token) : await GlossaryAsync(handle, id, token);
            ValidateRevision(snapshot.Revision, handle);
            snapshots.Add(snapshot with { Revision = snapshot.Revision with { ProtectedContent = snapshot.Revision.ProtectedContent!.ToArray() } });
        }
        if (snapshots.Sum(x => x.Revision.ProtectedContent!.Length) > MaximumSelectedEnvelopeBytes) throw Unavailable();
        if (expected is not null && (expected.Count != snapshots.Count
            || expected.Where((value, index) => !Same(value, snapshots[index])).Any())) throw Unavailable();
        return snapshots.ToArray();
    }

    private async Task<GroupBrainSnapshot> RequestAsync(GroupBatchClaimHandle handle, Guid id, CancellationToken token)
    {
        var scope = handle.Receipt.Scope;
        var head = await database.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.Id == id)
            .Select(x => new GroupBrainRequestHead(x.CurrentRevision, x.BusinessVersion, x.RequestCode, x.Kind,
                x.SourceVersion, x.DeletionGeneration, x.BusinessStatus, x.AssignedToUserId, x.CommittedDueAtUtc,
                x.ConfirmedByUserId, x.ConfirmedAtUtc, x.CreatedAtUtc, x.UpdatedAtUtc)).SingleOrDefaultAsync(token) ?? throw Unavailable();
        if (head.CurrentRevision <= 0 || head.BusinessVersion <= 0 || !Enum.IsDefined(head.Kind) || !Enum.IsDefined(head.BusinessStatus)
            || head.SourceVersion != handle.Authority.Source.Version || head.DeletionGeneration != handle.Authority.Source.DeletionGeneration
            || head.RequestCode is null || head.RequestCode.Length != 36 || !head.RequestCode.StartsWith("REQ-", StringComparison.Ordinal)
            || head.RequestCode.AsSpan(4).ContainsAnyExcept("0123456789ABCDEF")
            || head.CreatedAtUtc.Offset != TimeSpan.Zero || head.UpdatedAtUtc.Offset != TimeSpan.Zero || head.UpdatedAtUtc < head.CreatedAtUtc
            || (head.ConfirmedByUserId is null) != (head.ConfirmedAtUtc is null)
            || head.ConfirmedByUserId == Guid.Empty || head.AssignedToUserId == Guid.Empty
            || (head.ConfirmedAtUtc is { } confirmed && (confirmed.Offset != TimeSpan.Zero || confirmed < head.CreatedAtUtc))
            || (head.CommittedDueAtUtc is { } due && due.Offset != TimeSpan.Zero)
            || (head.ConfirmedByUserId is null && (head.BusinessStatus is not (GroupNoteBusinessStatus.New or GroupNoteBusinessStatus.NeedsClarification)
                || head.AssignedToUserId is not null || head.CommittedDueAtUtc is not null))) throw Unavailable();
        var revision = await RequestRevisionAsync(scope, id, head.CurrentRevision, token) ?? throw Unavailable();
        if (revision.CreatedAtUtc < head.CreatedAtUtc || revision.CreatedAtUtc > head.UpdatedAtUtc) throw Unavailable();
        if (revision.Origin is GroupRequestRevisionOrigin.AiExtracted or GroupRequestRevisionOrigin.HostAttention)
        {
            var expectedLevel = revision.Origin == GroupRequestRevisionOrigin.AiExtracted
                ? GroupRequestVerificationLevel.SourceBackedAiInterpretation : GroupRequestVerificationLevel.HostObserved;
            if (revision.VerificationLevel != expectedLevel
                || revision.AuthorServiceId is null || revision.AuthorServiceId == Guid.Empty
                || revision.AuthorUserId is not null || revision.SourceBatchId is null || revision.SourceBatchId == Guid.Empty
                || revision.ClaimEpoch is null or <= 0) throw Unavailable();
            if (revision.Origin == GroupRequestRevisionOrigin.HostAttention
                && (head.Kind is not (GroupNoteKind.NeedsClarification or GroupNoteKind.ExtractionFailed)
                    || head.BusinessStatus != GroupNoteBusinessStatus.NeedsClarification
                    || head.ConfirmedByUserId is not null)) throw Unavailable();
        }
        else if (revision.Origin != GroupRequestRevisionOrigin.ItEdited || revision.VerificationLevel != GroupRequestVerificationLevel.ItConfirmed
            || revision.AuthorServiceId is not null || revision.AuthorUserId is null || revision.AuthorUserId == Guid.Empty
            || revision.SourceBatchId is not null || revision.ClaimEpoch is not null) throw Unavailable();
        var evidence = await database.GroupRequestEvidence.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.RequestId == id && x.RequestRevision == head.CurrentRevision)
            .OrderBy(x => x.Ordinal).Take(11).Select(x => new GroupBrainEvidence(x.Ordinal, x.MessageId, x.MessageRevision, x.Kind)).ToArrayAsync(token);
        if (evidence.Length is < 1 or > 10) throw Unavailable();
        var winners = new List<GroupBrainEvidenceHead>();
        for (var index = 0; index < evidence.Length; index++)
        {
            var item = evidence[index];
            if (item.Ordinal != index + 1 || item.MessageId == Guid.Empty || item.MessageRevision <= 0 || !Enum.IsDefined(item.Kind)) throw Unavailable();
            if (revision.Origin == GroupRequestRevisionOrigin.HostAttention && item.Kind != GroupRequestEvidenceKind.HostMetadataAttention
                || revision.Origin == GroupRequestRevisionOrigin.AiExtracted && item.Kind != GroupRequestEvidenceKind.LiteralSourceQuote) throw Unavailable();
            // Project only bounded source dependency metadata. Original external
            // identities/private source bodies are not part of the brain read.
            var winner = await database.GroupMessageRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.MessageId == item.MessageId)
                .OrderByDescending(x => x.Kind == GroupSourceEventKind.Recall ? 3 : x.Kind == GroupSourceEventKind.Edit ? 2 : 1)
                .ThenByDescending(x => x.Revision).Select(x => new GroupBrainEvidenceHead(x.MessageId, x.Revision, x.Kind,
                    x.SourceVersion, x.DeletionGeneration, x.ContentSha256, x.CommittedSequence, x.OccurredAtUtc, x.CommittedAtUtc))
                .FirstOrDefaultAsync(token) ?? throw Unavailable();
            if (winner.Revision != item.MessageRevision || winner.Kind == GroupSourceEventKind.Recall || !Enum.IsDefined(winner.Kind)
                || winner.SourceVersion != handle.Authority.Source.Version || winner.DeletionGeneration != handle.Authority.Source.DeletionGeneration
                || winner.CommittedSequence <= 0 || winner.OccurredAtUtc.Offset != TimeSpan.Zero || winner.CommittedAtUtc.Offset != TimeSpan.Zero
                || winner.ContentSha256 is null || winner.ContentSha256.Length != 64 || winner.ContentSha256.AsSpan().ContainsAnyExcept("0123456789ABCDEF"))
                throw Unavailable();
            winners.Add(winner);
        }
        return new(GroupBrainContentKind.RequestRevision, id, head, null, revision, evidence, winners.ToArray());
    }

    private async Task<GroupBrainSnapshot> GlossaryAsync(GroupBatchClaimHandle handle, Guid id, CancellationToken token)
    {
        var scope = handle.Receipt.Scope;
        var head = await database.GroupGlossaryEntries.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.Id == id)
            .Select(x => new GroupBrainGlossaryHead(x.CurrentRevision, x.Version, x.SourceVersion, x.DeletionGeneration,
                x.IsEnabled, x.AllowExtraction, x.PublishedByUserId, x.CreatedAtUtc)).SingleOrDefaultAsync(token) ?? throw Unavailable();
        if (head.CurrentRevision <= 0 || head.Version <= 0 || !head.IsEnabled || !head.AllowExtraction || head.PublishedByUserId == Guid.Empty
            || head.SourceVersion != handle.Authority.Source.Version || head.DeletionGeneration != handle.Authority.Source.DeletionGeneration
            || head.CreatedAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
        var revision = await GlossaryRevisionAsync(scope, id, head.CurrentRevision, token) ?? throw Unavailable();
        if (revision.AuthorUserId != head.PublishedByUserId || revision.CreatedAtUtc < head.CreatedAtUtc) throw Unavailable();
        return new(GroupBrainContentKind.GlossaryRevision, id, null, head, revision, [], []);
    }

    private Task<GroupBrainStoredRevision?> RequestRevisionAsync(GroupScope scope, Guid id, long revision, CancellationToken token)
    {
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<GroupBrainStoredRevision>($"""
            SELECT TenantId,CompanyId,BindingId,RequestId AS RecordId,Revision,Origin,VerificationLevel,AuthorServiceId,AuthorUserId,SourceBatchId,ClaimEpoch,
              SourceVersion,DeletionGeneration,ContentKeyId,EnvelopeSha256,CreatedAtUtc,
              CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 30 AND 64029 THEN ProtectedContent END AS ProtectedContent
            FROM aioffice.GroupRequestRevisions
            """) : database.GroupRequestRevisions.AsNoTracking().Select(x => new GroupBrainStoredRevision(x.TenantId, x.CompanyId, x.BindingId,
                x.RequestId, x.Revision, x.Origin, x.VerificationLevel, x.AuthorServiceId, x.AuthorUserId, x.SourceBatchId, x.ClaimEpoch,
                x.SourceVersion, x.DeletionGeneration, x.ContentKeyId, x.EnvelopeSha256, x.CreatedAtUtc, x.ProtectedContent));
        return rows.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.RecordId == id && x.Revision == revision, token);
    }

    private Task<GroupBrainStoredRevision?> GlossaryRevisionAsync(GroupScope scope, Guid id, long revision, CancellationToken token)
    {
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<GroupBrainStoredRevision>($"""
            SELECT TenantId,CompanyId,BindingId,EntryId AS RecordId,Revision,
              CAST(2 AS int) AS Origin,CAST(2 AS int) AS VerificationLevel,CAST(NULL AS uniqueidentifier) AS AuthorServiceId,
              PublishedByUserId AS AuthorUserId,CAST(NULL AS uniqueidentifier) AS SourceBatchId,CAST(NULL AS bigint) AS ClaimEpoch,
              SourceVersion,DeletionGeneration,ContentKeyId,EnvelopeSha256,CreatedAtUtc,
              CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 30 AND 64029 THEN ProtectedContent END AS ProtectedContent
            FROM aioffice.GroupGlossaryRevisions
            """) : database.GroupGlossaryRevisions.AsNoTracking().Select(x => new GroupBrainStoredRevision(x.TenantId, x.CompanyId, x.BindingId,
                x.EntryId, x.Revision, GroupRequestRevisionOrigin.ItEdited, GroupRequestVerificationLevel.ItConfirmed, null, x.PublishedByUserId,
                null, null, x.SourceVersion, x.DeletionGeneration, x.ContentKeyId, x.EnvelopeSha256, x.CreatedAtUtc, x.ProtectedContent));
        return rows.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.RecordId == id && x.Revision == revision, token);
    }

    private static void ValidateRevision(GroupBrainStoredRevision revision, GroupBatchClaimHandle handle)
    {
        if (revision.Revision <= 0 || revision.SourceVersion != handle.Authority.Source.Version
            || revision.DeletionGeneration != handle.Authority.Source.DeletionGeneration || revision.CreatedAtUtc.Offset != TimeSpan.Zero
            || string.IsNullOrEmpty(revision.ContentKeyId) || revision.ContentKeyId.Length > 64
            || revision.ContentKeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
            || revision.ProtectedContent is null || revision.ProtectedContent.Length is < 30 or > GroupBrainContentProtector.MaximumEnvelopeLength
            || revision.ProtectedContent[0] != 1 || revision.EnvelopeSha256 != Convert.ToHexString(SHA256.HashData(revision.ProtectedContent))) throw Unavailable();
    }
    private static bool Same(GroupBrainSnapshot first, GroupBrainSnapshot second) => first.Kind == second.Kind
        && first.RecordId == second.RecordId && first.Request == second.Request && first.Glossary == second.Glossary
        && first.Revision with { ProtectedContent = null } == second.Revision with { ProtectedContent = null }
        && first.Revision.ProtectedContent!.AsSpan().SequenceEqual(second.Revision.ProtectedContent)
        && first.Evidence.SequenceEqual(second.Evidence) && first.SourceHeads.SequenceEqual(second.SourceHeads);
    private static void ValidateHostPayload(GroupBrainSnapshot snapshot, string value)
    {
        if (snapshot.Request is null || snapshot.Revision.Origin != GroupRequestRevisionOrigin.HostAttention) return;
        try
        {
            var payload = GroupBrainPayloadCodec.DecodeHostAttention(value);
            if (!payload.SourceReferences.SequenceEqual(snapshot.Evidence.Select(x => new GroupHostAttentionReference(x.MessageId, x.MessageRevision)))
                || (payload.Reason == GroupHostAttentionReason.ExtractionFailed) != (snapshot.Request.Kind == GroupNoteKind.ExtractionFailed))
                throw Unavailable();
        }
        catch (InvalidOperationException) { throw Unavailable(); }
    }
    private static (GroupBrainContentKind Kind, Guid Id)[] Select(IReadOnlyList<Guid> requests, IReadOnlyList<Guid> glossary)
    {
        if (requests is null || glossary is null) throw Unavailable();
        // Enumerate directly so IList Count/indexer fast paths cannot silently
        // omit selected dependencies. Bound the combined actual sequence.
        var result = new List<(GroupBrainContentKind Kind, Guid Id)>(MaximumSelectedRevisions);
        Add(requests, GroupBrainContentKind.RequestRevision); Add(glossary, GroupBrainContentKind.GlossaryRevision);
        if (result.Any(x => x.Id == Guid.Empty) || result.Distinct().Count() != result.Count) throw Unavailable();
        return result.ToArray();

        void Add(IReadOnlyList<Guid> values, GroupBrainContentKind kind)
        {
            foreach (var id in values)
            {
                if (result.Count == MaximumSelectedRevisions) throw Unavailable();
                result.Add((kind, id));
            }
        }
    }
    private void ValidateEntry(GroupBatchClaimHandle handle, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested(); worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        if (database.ChangeTracker.HasChanges() || database.Database.CurrentTransaction is not null
            || database.Database.IsRelational() && !database.Database.IsSqlServer()) throw Unavailable();
    }
    private async ValueTask<GroupSourceKeyMaterial> ResolveKeyAsync(GroupScope scope, string keyId, CancellationToken token)
    {
        if (database.Database.CurrentTransaction is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        Task<GroupSourceKeyMaterial>? pending = null;
        try { pending = keys.ResolveReadAsync(scope, keyId, token).AsTask(); return await pending.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending is not null) _ = pending.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) completed.Result?.Dispose(); else _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        catch (Exception) { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Group brain context is unavailable.");
}

internal sealed record GroupBrainStoredRevision(Guid TenantId, Guid CompanyId, Guid BindingId, Guid RecordId, long Revision,
    GroupRequestRevisionOrigin Origin, GroupRequestVerificationLevel VerificationLevel, Guid? AuthorServiceId, Guid? AuthorUserId,
    Guid? SourceBatchId, long? ClaimEpoch, long SourceVersion, long DeletionGeneration, string ContentKeyId, string EnvelopeSha256,
    DateTimeOffset CreatedAtUtc, byte[]? ProtectedContent);
internal sealed record GroupBrainRequestHead(long CurrentRevision, long BusinessVersion, string RequestCode, GroupNoteKind Kind,
    long SourceVersion, long DeletionGeneration, GroupNoteBusinessStatus BusinessStatus, Guid? AssignedToUserId,
    DateTimeOffset? CommittedDueAtUtc, Guid? ConfirmedByUserId, DateTimeOffset? ConfirmedAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
internal sealed record GroupBrainGlossaryHead(long CurrentRevision, long Version, long SourceVersion, long DeletionGeneration,
    bool IsEnabled, bool AllowExtraction, Guid PublishedByUserId, DateTimeOffset CreatedAtUtc);
internal sealed record GroupBrainEvidence(int Ordinal, Guid MessageId, long MessageRevision, GroupRequestEvidenceKind Kind);
internal sealed record GroupBrainEvidenceHead(Guid MessageId, long Revision, GroupSourceEventKind Kind,
    long SourceVersion, long DeletionGeneration, string ContentSha256, long CommittedSequence, DateTimeOffset OccurredAtUtc, DateTimeOffset CommittedAtUtc);
internal sealed record GroupBrainSnapshot(GroupBrainContentKind Kind, Guid RecordId, GroupBrainRequestHead? Request,
    GroupBrainGlossaryHead? Glossary, GroupBrainStoredRevision Revision, GroupBrainEvidence[] Evidence, GroupBrainEvidenceHead[] SourceHeads);
