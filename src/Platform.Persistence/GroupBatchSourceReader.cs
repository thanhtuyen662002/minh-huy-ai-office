using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Separate trusted Extract path. It cannot borrow a portal Reader grant or
// construct an owner/user identity. Every selected ID belongs to this batch.
public sealed class GroupBatchSourceReader(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, IGroupSourceKeyProvider keys, GroupSourceContentProtector protector)
{
    internal bool UsesContext(PlatformDbContext context, GroupExtractionWorkerBinding binding, TimeProvider time) =>
        ReferenceEquals(database, context) && worker == binding && ReferenceEquals(clock, time);

    public async Task<GroupBatchSourceContext> ReadAsync(GroupBatchClaimHandle handle, IReadOnlyList<Guid> messageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle); ArgumentNullException.ThrowIfNull(messageIds);
        // Freeze actual selected identities without IList Count/indexer fast
        // paths: the source/evidence set must never be silently truncated.
        var actual = new List<Guid>(100);
        foreach (var id in messageIds)
        {
            if (actual.Count == 100) throw Unavailable();
            actual.Add(id);
        }
        var selected = actual.ToArray();
        if (selected.Length is < 1 or > 100 || selected.Any(x => x == Guid.Empty) || selected.Distinct().Count() != selected.Length)
            throw Unavailable();
        ValidateEntry(handle, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        try
        {
            var initial = await ReadUnitAsync(handle, selected, null, null, cancellationToken);
            var texts = new Dictionary<Guid, string>();
            foreach (var group in initial.Snapshots.Where(x => x.Disposition == GroupBatchSourceDisposition.Readable).GroupBy(x => x.Head.ContentKeyId))
            {
                // No SQL transaction/source/registry locks span key resolution.
                using var key = await ResolveKeyAsync(handle.Receipt.Scope, group.Key, cancellationToken);
                if (key.KeyId != group.Key) throw Unavailable();
                // Fresh authority/lease/dependencies BEFORE decrypt, followed
                // by a separate final release proof after all private work.
                await ReadUnitAsync(handle, selected, initial.Snapshots, initial.Coverage, cancellationToken);
                foreach (var source in group)
                {
                    var head = source.Head;
                    var text = protector.Unprotect(new(handle.Receipt.Scope, head.MessageId, head.Revision,
                        head.SourceVersion, head.DeletionGeneration), source.ProtectedContent ?? throw Unavailable(), key.Key, key.KeyId);
                    var metadata = new GroupSourceEventMetadata(handle.Authority.Source.ExternalIdentity, source.ExternalMessageId,
                        source.EventId, source.SenderId, source.ReplyToMessageId, head.Kind, head.OccurredAtUtc,
                        head.ContentSha256, head.IsHistoricalBackfill);
                    metadata.Validate();
                    var clear = new UTF8Encoding(false, true).GetBytes(text);
                    try { if (Convert.ToHexString(SHA256.HashData(clear)) != head.ContentSha256) throw Unavailable(); }
                    finally { CryptographicOperations.ZeroMemory(clear); }
                    if (GroupIngressStore.EnvelopeHash(metadata, text) != source.Receipt.EnvelopeSha256) throw Unavailable();
                    texts.Add(head.MessageId, text);
                }
            }
            await ReadUnitAsync(handle, selected, initial.Snapshots, initial.Coverage, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(handle, initial.Cutoff, initial.Coverage, initial.Snapshots, initial.Snapshots.Select(x =>
                new GroupBatchSourceEntry(x, texts.GetValueOrDefault(x.Head.MessageId))).ToArray());
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException or EncoderFallbackException)
        { throw Unavailable(); }
    }

    // Instant source-context fence for later provider release. Final note
    // effects still require their own live source-locked effect transaction.
    public async Task RequireCurrentAsync(GroupBatchSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateEntry(context.Handle, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        try
        {
            await ReadUnitAsync(context.Handle, context.Snapshots.Select(x => x.Head.MessageId).ToArray(),
                context.Snapshots, context.Coverage, cancellationToken);
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException or EncoderFallbackException)
        { throw Unavailable(); }
    }

    private async Task<(long Cutoff, GroupBatchCoverageSnapshot Coverage, GroupBatchSourceSnapshot[] Snapshots)> ReadUnitAsync(GroupBatchClaimHandle handle,
        Guid[] selected, IReadOnlyList<GroupBatchSourceSnapshot>? expected, GroupBatchCoverageSnapshot? expectedCoverage, CancellationToken token)
    {
        ValidateEntry(handle, token);
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token);
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var allocation = await FenceAsync();
        var current = await ReadLockedAsync(handle, selected, allocation, expected, token);
        if (expectedCoverage is not null && !expectedCoverage.Same(current.Coverage)) throw Unavailable();
        await FenceAsync();
        if (database.ChangeTracker.HasChanges()) throw Unavailable();
        await transaction.CommitAsync(token);
        return (allocation.AllocatedThroughSequence, current.Coverage, current.Snapshots);

        async Task<GroupBatchAllocationReceipt> FenceAsync()
        {
            var verdict = await claims.InspectCurrentLockedAsync(handle, token);
            if (verdict is GroupBatchClaimFenceVerdict.Current current) return current.Allocation;
            var expired = (GroupBatchClaimFenceVerdict.Expired)verdict;
            // This unit contains only reads: no effect has been staged/flushed.
            // Expiry writes metadata only, commits before denial, and never
            // relies on a rolled-back transaction to preserve its witness.
            await claims.RetireExpiredLockedAsync(expired.Observation, token);
            await transaction.CommitAsync(token);
            throw GroupServiceDirectory.Denied();
        }
    }

    // Fixed SQL dependency read for the future owned effect unit. It returns
    // an expiry observation; it never retires/commits that unit's staged writes.
    internal async Task<GroupBatchClaimFenceVerdict> RequireUnchangedLockedAsync(GroupBatchSourceContext context,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateLockedEntry(context.Handle, token);
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var verdict = await claims.InspectCurrentLockedAsync(context.Handle, token);
        if (verdict is GroupBatchClaimFenceVerdict.Expired) return verdict;
        var allocation = ((GroupBatchClaimFenceVerdict.Current)verdict).Allocation;
        var current = await ReadLockedAsync(context.Handle, context.Snapshots.Select(x => x.Head.MessageId).ToArray(),
            allocation, context.Snapshots, token);
        if (allocation.AllocatedThroughSequence != context.AllocatedThroughSequence || !context.Coverage.Same(current.Coverage))
            throw Unavailable();
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

    // Current claim/authority and every original contributing source/cipher/
    // coverage dependency. This owns no commit or expiry witness. A future
    // terminal consumer must own its transaction and staged-effect rollback.
    internal async Task<GroupBatchClaimFenceVerdict> RequireManifestUnchangedLockedAsync(GroupBatchClaimHandle handle,
        GroupWorkDependencyManifest manifest, CancellationToken token)
    {
        ValidateLockedEntry(handle, token);
        if (manifest.Scope != handle.Receipt.Scope || manifest.BatchId != handle.Receipt.BatchId
            || manifest.AuthoritySha256 != GroupBatchClaimStore.AuthorityFingerprint(handle.Authority)) throw Unavailable();
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var permissions = new GroupWorkNotePermissionVerifier(database);
        await permissions.RequireSafeRuntimeAsync(token);
        var verdict = await claims.InspectCurrentLockedAsync(handle, token);
        if (verdict is GroupBatchClaimFenceVerdict.Expired) return verdict;
        var allocation = ((GroupBatchClaimFenceVerdict.Current)verdict).Allocation;
        if (allocation.AllocatedThroughSequence != manifest.AllocatedThroughSequence) throw Unavailable();
        var current = await ReadLockedAsync(handle, manifest.Sources.Select(x => x.MessageId).ToArray(), allocation, null, token);
        if (current.Coverage.Fingerprint() != manifest.CoverageSha256 || current.Snapshots.Length != manifest.Sources.Count
            || current.Snapshots.Where((snapshot, index) => snapshot.Head.Revision != manifest.Sources[index].Revision
                || GroupWorkDependencyManifest.SourceFingerprint(snapshot) != manifest.Sources[index].SnapshotSha256).Any()) throw Unavailable();
        await permissions.RequireSafeRuntimeAsync(token);
        return await claims.InspectCurrentLockedAsync(handle, token);
    }

    private async Task<(GroupBatchCoverageSnapshot Coverage, GroupBatchSourceSnapshot[] Snapshots)> ReadLockedAsync(GroupBatchClaimHandle handle,
        Guid[] selected, GroupBatchAllocationReceipt allocation, IReadOnlyList<GroupBatchSourceSnapshot>? expected, CancellationToken token)
    {
        if (selected.Any(id => !allocation.Revisions.Any(x => x.Metadata.MessageId == id))) throw Unavailable();
        var snapshots = new List<GroupBatchSourceSnapshot>();
        foreach (var id in selected)
        {
            var scope = handle.Receipt.Scope;
            var head = await HeadAsync(scope, id, allocation.AllocatedThroughSequence, token) ?? throw Unavailable();
            var current = await HeadAsync(scope, id, long.MaxValue, token) ?? throw Unavailable();
            ValidateHead(head); ValidateHead(current);
            if (head.CommittedAtUtc > allocation.AllocatedAtUtc) throw Unavailable();
            var message = await MessageAsync(scope, id, token) ?? throw Unavailable();
            var external = Text(message.ExternalBytes, message.ExternalText);
            if (message.IdentityHash != GroupIngressIdentity.MessageIndex(scope, external)) throw Unavailable();
            var eventId = head.EventText!;
            var receipt = await ReceiptAsync(scope, eventId, token) ?? throw Unavailable();
            if (receipt.EventIdentityHash != GroupIngressIdentity.EventIndex(scope, eventId) || receipt.MessageId != id
                || receipt.Revision != head.Revision || Text(receipt.EventBytes, receipt.EventText) != eventId
                || !Hex(receipt.EnvelopeSha256) || receipt.ServiceId == Guid.Empty || receipt.CredentialEpoch <= 0
                || receipt.ListenerEpoch <= 0 || receipt.CommittedAtUtc != head.CommittedAtUtc
                || receipt.CommittedAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
            receipt = receipt with { EventBytes = null, EventText = eventId };
            var disposition = head != current ? GroupBatchSourceDisposition.ChangedAfterCutoff
                : head.SourceVersion != handle.Authority.Source.Version || head.DeletionGeneration != handle.Authority.Source.DeletionGeneration
                    ? GroupBatchSourceDisposition.ObsoleteGeneration
                    : head.Kind == GroupSourceEventKind.Recall ? GroupBatchSourceDisposition.Recalled : GroupBatchSourceDisposition.Readable;
            byte[]? cipher = null;
            if (disposition == GroupBatchSourceDisposition.Readable)
            {
                var stored = await CipherAsync(scope, id, head.Revision, token) ?? throw Unavailable();
                cipher = stored.ProtectedContent?.ToArray();
                if (cipher is null || cipher.Length is < 29 or > GroupSourceContentProtector.MaximumEnvelopeLength) throw Unavailable();
            }
            snapshots.Add(new(head, current, external, head.SenderText!, head.ReplyText, eventId, receipt, disposition, cipher));
        }
        if (expected is not null && (expected.Count != snapshots.Count
            || expected.Where((source, index) => !Same(source, snapshots[index])).Any())) throw Unavailable();
        var sourceScope = handle.Receipt.Scope;
        var gap = await GroupBatchCoverageSnapshot.ReadAsync(database, sourceScope, handle.Authority.Source.ConnectorAccountId, token);
        return (gap, snapshots.ToArray());
    }

    private async ValueTask<GroupSourceKeyMaterial> ResolveKeyAsync(GroupScope scope, string keyId, CancellationToken token)
    {
        if (database.Database.CurrentTransaction is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        Task<GroupSourceKeyMaterial>? pending = null;
        try
        {
            pending = keys.ResolveReadAsync(scope, keyId, token).AsTask();
            return await pending.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A resolver may ignore cancellation. Never wait indefinitely or
            // release its late key; observe faults and dispose late material.
            if (pending is not null)
                _ = pending.ContinueWith(completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
                    else _ = completed.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        catch (Exception) { throw Unavailable(); }
    }

    private async Task<GroupBatchSourceHead?> HeadAsync(GroupScope scope, Guid message, long cutoff, CancellationToken token)
    {
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<GroupBatchSourceHead>($"""
            SELECT TenantId,CompanyId,BindingId,MessageId,Revision,CommittedSequence,Kind,SourceVersion,DeletionGeneration,OccurredAtUtc,CommittedAtUtc,IsHistoricalBackfill,
              CASE WHEN DATALENGTH(ContentSha256)=64 THEN ContentSha256 END AS ContentSha256,
              CASE WHEN DATALENGTH(ContentKeyId)<=128 THEN ContentKeyId END AS ContentKeyId,
              CASE WHEN DATALENGTH(ExternalRevisionEventId)<=512 THEN CONVERT(varbinary(512),ExternalRevisionEventId) END AS EventBytes,
              CASE WHEN DATALENGTH(SenderId)<=512 THEN CONVERT(varbinary(512),SenderId) END AS SenderBytes,
              CASE WHEN DATALENGTH(ReplyToMessageId)<=512 THEN CONVERT(varbinary(512),ReplyToMessageId) END AS ReplyBytes,
              CAST(CASE WHEN ReplyToMessageId IS NULL THEN 1 ELSE 0 END AS bit) AS ReplyIsNull,
              CAST(NULL AS nvarchar(256)) AS EventText,CAST(NULL AS nvarchar(256)) AS SenderText,CAST(NULL AS nvarchar(256)) AS ReplyText
            FROM aioffice.GroupMessageRevisions
            """) : database.GroupMessageRevisions.AsNoTracking().Select(x => new GroupBatchSourceHead(x.TenantId, x.CompanyId, x.BindingId,
                x.MessageId, x.Revision, x.CommittedSequence, x.Kind, x.ContentSha256, x.ContentKeyId, x.SourceVersion, x.DeletionGeneration,
                x.OccurredAtUtc, x.CommittedAtUtc, x.IsHistoricalBackfill, null, null, null, x.ReplyToMessageId == null,
                x.ExternalRevisionEventId, x.SenderId, x.ReplyToMessageId));
        var row = await rows.Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.MessageId == message && x.CommittedSequence <= cutoff)
            .OrderByDescending(x => x.Kind == GroupSourceEventKind.Recall ? 3 : x.Kind == GroupSourceEventKind.Edit ? 2 : 1)
            .ThenByDescending(x => x.Revision).FirstOrDefaultAsync(token);
        if (row is null) return null;
        var reply = row.ReplyIsNull ? null : Text(row.ReplyBytes, row.ReplyText);
        if (row.ReplyIsNull && (row.ReplyBytes is not null || row.ReplyText is not null)) throw Unavailable();
        return row with
        {
            EventText = Text(row.EventBytes, row.EventText),
            SenderText = Text(row.SenderBytes, row.SenderText),
            ReplyText = reply,
            EventBytes = null,
            SenderBytes = null,
            ReplyBytes = null
        };
    }

    private async Task<StoredMessage?> MessageAsync(GroupScope scope, Guid id, CancellationToken token)
    {
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<StoredMessage>($"""
            SELECT TenantId,CompanyId,BindingId,Id,
              CASE WHEN DATALENGTH(IdentityHash)=64 THEN IdentityHash END AS IdentityHash,
              CASE WHEN DATALENGTH(ExternalMessageId)<=512 THEN CONVERT(varbinary(512),ExternalMessageId) END AS ExternalBytes,
              CAST(NULL AS nvarchar(256)) AS ExternalText FROM aioffice.GroupMessages
            """) : database.GroupMessages.AsNoTracking().Select(x => new StoredMessage(x.TenantId, x.CompanyId, x.BindingId,
                x.Id, x.IdentityHash, null, x.ExternalMessageId));
        return await rows.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.Id == id, token);
    }

    private async Task<GroupBatchSourceReceipt?> ReceiptAsync(GroupScope scope, string eventId, CancellationToken token)
    {
        var hash = GroupIngressIdentity.EventIndex(scope, eventId);
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<GroupBatchSourceReceipt>($"""
            SELECT TenantId,CompanyId,BindingId,MessageId,Revision,ServiceId,CredentialEpoch,ListenerEpoch,CommittedAtUtc,
              CASE WHEN DATALENGTH(EventIdentityHash)=64 THEN EventIdentityHash END AS EventIdentityHash,
              CASE WHEN DATALENGTH(EnvelopeSha256)=64 THEN EnvelopeSha256 END AS EnvelopeSha256,
              CASE WHEN DATALENGTH(ExternalRevisionEventId)<=512 THEN CONVERT(varbinary(512),ExternalRevisionEventId) END AS EventBytes,
              CAST(NULL AS nvarchar(256)) AS EventText FROM aioffice.GroupIngressReceipts
            """) : database.GroupIngressReceipts.AsNoTracking().Select(x => new GroupBatchSourceReceipt(x.TenantId, x.CompanyId,
                x.BindingId, x.EventIdentityHash, x.MessageId, x.Revision, x.EnvelopeSha256, x.ServiceId, x.CredentialEpoch,
                x.ListenerEpoch, x.CommittedAtUtc, null, x.ExternalRevisionEventId));
        return await rows.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.EventIdentityHash == hash, token);
    }

    private async Task<StoredCipher?> CipherAsync(GroupScope scope, Guid message, long revision, CancellationToken token)
    {
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<StoredCipher>($"""
            SELECT TenantId,CompanyId,BindingId,MessageId,Revision,
              CASE WHEN DATALENGTH(ProtectedContent)<=65536 THEN ProtectedContent END AS ProtectedContent
            FROM aioffice.GroupMessageRevisions
            """) : database.GroupMessageRevisions.AsNoTracking().Select(x => new StoredCipher(x.TenantId, x.CompanyId, x.BindingId,
                x.MessageId, x.Revision, x.ProtectedContent));
        return await rows.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.MessageId == message && x.Revision == revision, token);
    }

    private void ValidateEntry(GroupBatchClaimHandle handle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId)
            throw GroupServiceDirectory.Denied();
        if (database.ChangeTracker.HasChanges() || database.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null
            || database.Database.IsRelational() && !database.Database.IsSqlServer()) throw Unavailable();
    }

    private static void ValidateHead(GroupBatchSourceHead head)
    {
        if (head.Revision <= 0 || head.CommittedSequence <= 0 || !Enum.IsDefined(head.Kind) || !Hex(head.ContentSha256)
            || head.SourceVersion <= 0 || head.DeletionGeneration < 0 || head.OccurredAtUtc.Offset != TimeSpan.Zero
            || head.CommittedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrEmpty(head.ContentKeyId) || head.ContentKeyId.Length > 64
            || head.ContentKeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw Unavailable();
    }
    private static bool Same(GroupBatchSourceSnapshot left, GroupBatchSourceSnapshot right) =>
        left.Head == right.Head && left.CurrentHead == right.CurrentHead && left.ExternalMessageId == right.ExternalMessageId
        && left.Receipt == right.Receipt && left.Disposition == right.Disposition
        && (left.ProtectedContent is null ? right.ProtectedContent is null : right.ProtectedContent is not null
            && left.ProtectedContent.AsSpan().SequenceEqual(right.ProtectedContent));
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(c => "0123456789ABCDEF".Contains(c));
    private static string Text(byte[]? bytes, string? text)
    {
        var value = bytes is null ? text ?? throw Unavailable() : GroupRegistryReader.Decode(bytes, 512);
        GroupExternalIdentity.ValidateOpaqueId(value); return value;
    }
    private static InvalidOperationException Unavailable() => new("Group batch source context is not available.");
    private sealed record StoredMessage(Guid TenantId, Guid CompanyId, Guid BindingId, Guid Id, string IdentityHash, byte[]? ExternalBytes, string? ExternalText);
    private sealed record StoredCipher(Guid TenantId, Guid CompanyId, Guid BindingId, Guid MessageId, long Revision, byte[]? ProtectedContent);
}
