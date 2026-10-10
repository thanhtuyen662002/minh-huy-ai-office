using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupNoteCommitResult(GroupScope Scope, Guid BatchId, Guid OperationId,
    Guid OutboxId, IReadOnlyList<Guid> RequestIds, DateTimeOffset CommittedAtUtc, bool WasAlreadyCommitted);

// Fixed creation consumer; it never updates an existing business request or
// treats the model's relation hint, deadline or interpretation as IT authority.
public sealed class GroupNoteCommitStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain,
    IGroupSourceKeyProvider keys, GroupBrainContentProtector protector)
{
    private const string EffectSavepoint = "aioffice_group_note_effect";

    public async Task<GroupNoteCommitResult> CommitAsync(GroupGroundedWorkProposal proposal,
        GroupBrainPrivateContext dependencies, Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal); ArgumentNullException.ThrowIfNull(dependencies);
        ValidateEntry(proposal, dependencies, operationId, cancellationToken);
        return await CommitCoreAsync(GroupNoteEffectPlan.FromProposal(proposal), dependencies, operationId, cancellationToken);
    }

    public async Task<GroupNoteCommitResult> CommitAutomaticAsync(GroupAutomaticNotePlan plan,
        GroupBrainPrivateContext dependencies, Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(dependencies);
        ValidateAutomaticEntry(plan, dependencies, operationId, cancellationToken);
        return await CommitCoreAsync(GroupNoteEffectPlan.FromAutomatic(plan), dependencies, operationId, cancellationToken);
    }

    private async Task<GroupNoteCommitResult> CommitCoreAsync(GroupNoteEffectPlan plan,
        GroupBrainPrivateContext dependencies, Guid operationId, CancellationToken cancellationToken)
    {
        var context = plan.Preparation.Context; var handle = context.Handle; var scope = context.Scope;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        var payloads = plan.Notes.Select(x => x.Payload).ToArray();
        if (payloads.Sum(x => Encoding.UTF8.GetByteCount(x) + 29) > GroupBrainCurrentReader.MaximumSelectedEnvelopeBytes) throw Unavailable();
        var requestIds = payloads.Select((_, index) => Identity("request", index + 1)).ToArray();
        var outboxId = Identity("outbox", 0);
        var selected = plan.Selected.ToDictionary(x => x.MessageId);
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\n",
            selected.OrderBy(x => x.Key).Select(x => x.Key.ToString("D") + "/" + x.Value.Revision.ToString(CultureInfo.InvariantCulture))))));
        var historical = context.Items.Any(x => x.IsHistoricalBackfill);
        var staged = new List<object>(); var resolved = new Dictionary<string, GroupSourceKeyMaterial>(StringComparer.Ordinal);
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var permissions = new GroupWorkNotePermissionVerifier(database);
        DataSourceRegistrationTransaction? active = null; IDbContextTransaction? sql = null;
        var savepointCreated = false; DateTimeOffset? effectTime = null;
        GroupBatchAllocationReceipt? currentAllocation = null;
        try
        {
            string[] retainedKeyIds;
            bool originalExisted;
            // Release the owned SQL transaction and locks before external key
            // awaits. An already-open caller connection remains caller-owned.
            await using (var preflight = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken))
            {
                active = preflight; sql = database.Database.CurrentTransaction;
                if (sql is null || !sql.SupportsSavepoints) throw Unavailable();
                await FenceAsync();
                var prior = await OriginalAsync(); originalExisted = prior is not null;
                retainedKeyIds = [];
                if (prior is not null)
                {
                    ValidateReceipt(prior);
                    var metadata = await database.GroupRequestRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                        && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && requestIds.Contains(x.RequestId)
                        && x.Revision == 1).Select(x => new
                        {
                            x.RequestId,
                            x.ContentKeyId,
                            Length = EF.Functions.DataLength(x.ProtectedContent)
                        }).Take(plan.MaximumNotes + 1).ToArrayAsync(cancellationToken);
                    if (metadata.Length != requestIds.Length || metadata.Select(x => x.RequestId).Distinct().Count() != metadata.Length
                        || metadata.Any(x => x.Length is not (>= 30 and <= GroupBrainContentProtector.MaximumEnvelopeLength)
                            || !ValidKeyId(x.ContentKeyId))) throw Unavailable();
                    retainedKeyIds = metadata.Select(x => x.ContentKeyId).Distinct(StringComparer.Ordinal).ToArray();
                }
                else await RequireUnclassifiedAsync();
                await FenceAsync(); await preflight.CommitAsync(cancellationToken);
            }
            active = null; sql = null;
            if (originalExisted)
            {
                foreach (var id in retainedKeyIds)
                {
                    var key = await ResolveKeyAsync(id);
                    if (key.KeyId != id) { key.Dispose(); throw Unavailable(); }
                    resolved.Add(id, key);
                }
            }
            else
            {
                var key = await ResolveKeyAsync(null); resolved.Add(key.KeyId, key);
            }
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            active = transaction; sql = database.Database.CurrentTransaction;
            if (sql is null || !sql.SupportsSavepoints) throw Unavailable();
            // Fences acquire the transaction-owned source lock before savepoint.
            await FenceAsync();
            var original = await OriginalAsync();
            if (original is not null)
            {
                await ValidateReplayAsync(original);
                await FenceAsync(); await transaction.CommitAsync(cancellationToken);
                return Result(original, true);
            }
            if (originalExisted) throw Unavailable();
            await RequireUnclassifiedAsync();
            await sql.CreateSavepointAsync(EffectSavepoint, cancellationToken); savepointCreated = true;
            var now = UtcNow(); if (now < handle.Receipt.IssuedAtUtc) throw Unavailable(); effectTime = now;
            var receipt = new GroupWorkCommitReceiptRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = context.BatchId,
                OperationId = operationId,
                SourceSetSha256 = sourceHash,
                SelectedMessageCount = selected.Count,
                NoteCount = requestIds.Length,
                Outcome = plan.Outcome,
                ServiceId = handle.Receipt.ServiceId,
                ClaimEpoch = handle.Receipt.Epoch,
                CredentialEpoch = handle.Receipt.CredentialEpoch,
                GrantVersion = handle.Receipt.GrantVersion,
                SourceVersion = handle.Receipt.SourceVersion,
                DeletionGeneration = handle.Receipt.DeletionGeneration,
                AccountVersion = handle.Receipt.AccountVersion,
                CommittedAtUtc = now
            };
            staged.Add(receipt);
            var writeKey = resolved.Values.Single();
            for (var index = 0; index < requestIds.Length; index++)
            {
                var id = requestIds[index]; var note = plan.Notes[index];
                var envelope = protector.Protect(new(scope, GroupBrainContentKind.RequestRevision, id, 1,
                    handle.Receipt.SourceVersion, handle.Receipt.DeletionGeneration), payloads[index], writeKey.Key, writeKey.KeyId);
                staged.Add(new GroupCustomerRequestRecord
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    Id = id,
                    OriginBatchId = context.BatchId,
                    OriginOperationId = operationId,
                    OriginCandidateOrdinal = index + 1,
                    RequestCode = "REQ-" + id.ToString("N").ToUpperInvariant(),
                    Kind = note.Kind,
                    SourceVersion = handle.Receipt.SourceVersion,
                    DeletionGeneration = handle.Receipt.DeletionGeneration,
                    CurrentRevision = 1,
                    BusinessVersion = 1,
                    BusinessStatus = note.BusinessStatus,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                staged.Add(new GroupRequestRevisionRecord
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    RequestId = id,
                    Revision = 1,
                    Origin = note.Origin,
                    VerificationLevel = note.VerificationLevel,
                    AuthorServiceId = handle.Receipt.ServiceId,
                    SourceBatchId = context.BatchId,
                    ClaimEpoch = handle.Receipt.Epoch,
                    SourceVersion = handle.Receipt.SourceVersion,
                    DeletionGeneration = handle.Receipt.DeletionGeneration,
                    ContentKeyId = writeKey.KeyId,
                    ProtectedContent = envelope,
                    EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(envelope)),
                    CreatedAtUtc = now
                });
                staged.AddRange(note.Evidence.Select((reference, ordinal) => new GroupRequestEvidenceRecord
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    RequestId = id,
                    RequestRevision = 1,
                    Ordinal = ordinal + 1,
                    MessageId = reference.MessageId,
                    MessageRevision = reference.Revision,
                    Kind = note.EvidenceKind
                }));
                staged.Add(new GroupNotesCommittedItemRecord
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    OutboxId = outboxId,
                    Ordinal = index + 1,
                    RequestId = id,
                    RequestRevision = 1
                });
            }
            staged.AddRange(selected.Values.Select(x => new GroupWorkSourceDispositionRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = context.BatchId,
                OperationId = operationId,
                MessageId = x.MessageId,
                MessageRevision = x.Revision,
                Outcome = x.Outcome
            }));
            if (plan.IsAutomatic) staged.AddRange(RawAccounting());
            staged.Add(new GroupNotesCommittedOutboxRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                Id = outboxId,
                BatchId = context.BatchId,
                OperationId = operationId,
                NoteCount = requestIds.Length,
                IsHistoricalBackfill = historical,
                CommittedAtUtc = now,
                AvailableAtUtc = now
            });
            database.AddRange(staged); await database.SaveChangesAsync(cancellationToken);
            await FenceAsync(); if (database.ChangeTracker.HasChanges()) throw Unavailable();
            await transaction.CommitAsync(cancellationToken); return Result(receipt, false);
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or InvalidOperationException or ArgumentException)
        { throw Unavailable(); }
        finally { DetachStaged(); foreach (var key in resolved.Values) key.Dispose(); }

        async Task<GroupWorkCommitReceiptRecord?> OriginalAsync() => await database.GroupWorkCommitReceipts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.OperationId == operationId, cancellationToken);
        async Task RequireUnclassifiedAsync()
        {
            var ids = selected.Keys.ToArray();
            if (await database.GroupWorkSourceDispositions.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == context.BatchId
                && ids.Contains(x.MessageId), cancellationToken)) throw Unavailable();
        }
        async Task FenceAsync()
        {
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await ObserveAsync(await claims.InspectCurrentLockedAsync(handle, cancellationToken));
            await ObserveAsync(await sources.RequireUnchangedLockedAsync(context, cancellationToken));
            await ObserveAsync(await brain.RequireUnchangedLockedAsync(dependencies, cancellationToken));
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            if (effectTime is { } minimum && UtcNow() < minimum) throw GroupServiceDirectory.Denied();
            await ObserveAsync(await claims.InspectCurrentLockedAsync(handle, cancellationToken));
        }
        async Task ObserveAsync(GroupBatchClaimFenceVerdict verdict)
        {
            if (verdict is GroupBatchClaimFenceVerdict.Current current) currentAllocation = current.Allocation;
            if (verdict is not GroupBatchClaimFenceVerdict.Expired expired) return;
            if (savepointCreated) { await sql!.RollbackToSavepointAsync(EffectSavepoint, cancellationToken); DetachStaged(); }
            if (database.ChangeTracker.HasChanges()) throw Unavailable();
            await claims.RetireExpiredLockedAsync(expired.Observation, cancellationToken);
            await permissions.RequireSafeRuntimeAsync(cancellationToken); await active!.CommitAsync(cancellationToken);
            throw GroupServiceDirectory.Denied();
        }
        void ValidateReceipt(GroupWorkCommitReceiptRecord original)
        {
            if (original.BatchId != context.BatchId || original.SourceSetSha256 != sourceHash || original.SelectedMessageCount != selected.Count
                || original.NoteCount != payloads.Length || original.Outcome != plan.Outcome
                || original.ServiceId != handle.Receipt.ServiceId || original.ClaimEpoch <= 0 || original.ClaimEpoch > handle.Receipt.Epoch
                || original.CredentialEpoch != handle.Receipt.CredentialEpoch || original.GrantVersion != handle.Receipt.GrantVersion
                || original.SourceVersion != handle.Receipt.SourceVersion || original.DeletionGeneration != handle.Receipt.DeletionGeneration
                || original.AccountVersion != handle.Receipt.AccountVersion || original.CommittedAtUtc.Offset != TimeSpan.Zero
                || original.CommittedAtUtc > UtcNow()) throw Unavailable();
        }
        async Task ValidateReplayAsync(GroupWorkCommitReceiptRecord original)
        {
            ValidateReceipt(original);
            var dispositions = await database.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == context.BatchId
                && x.OperationId == operationId).Take(101).ToArrayAsync(cancellationToken);
            if (dispositions.Length != selected.Count || dispositions.Select(x => x.MessageId).Distinct().Count() != dispositions.Length
                || dispositions.Any(x => !selected.TryGetValue(x.MessageId, out var source) || source.Revision != x.MessageRevision
                    || x.Outcome != source.Outcome)) throw Unavailable();
            if (plan.IsAutomatic) await GroupWorkRawAccounting.RequireReplayAsync(database, scope, operationId, RawAccounting(), cancellationToken);
            var requests = await database.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OriginBatchId == context.BatchId
                && x.OriginOperationId == operationId).OrderBy(x => x.OriginCandidateOrdinal).Take(plan.MaximumNotes + 1).ToArrayAsync(cancellationToken);
            if (requests.Length != requestIds.Length) throw Unavailable();
            var revisions = await database.GroupRequestRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && requestIds.Contains(x.RequestId) && x.Revision == 1
                && EF.Functions.DataLength(x.ProtectedContent) >= 30
                && EF.Functions.DataLength(x.ProtectedContent) <= GroupBrainContentProtector.MaximumEnvelopeLength).Take(plan.MaximumNotes + 1).ToArrayAsync(cancellationToken);
            if (revisions.Length != requestIds.Length || revisions.Sum(x => x.ProtectedContent.Length) > GroupBrainCurrentReader.MaximumSelectedEnvelopeBytes) throw Unavailable();
            var evidence = await database.GroupRequestEvidence.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && requestIds.Contains(x.RequestId)
                && x.RequestRevision == 1).Take(plan.MaximumEvidenceRows + 1).ToArrayAsync(cancellationToken);
            if (evidence.Length != plan.Notes.Sum(x => x.Evidence.Count)) throw Unavailable();
            for (var index = 0; index < requests.Length; index++)
            {
                var id = requestIds[index]; var head = requests[index]; var note = plan.Notes[index];
                if (head.Id != id || head.OriginCandidateOrdinal != index + 1 || head.RequestCode != "REQ-" + id.ToString("N").ToUpperInvariant()
                    || head.Kind != note.Kind || head.SourceVersion != original.SourceVersion || head.DeletionGeneration != original.DeletionGeneration
                    || head.CreatedAtUtc != original.CommittedAtUtc) throw Unavailable();
                var revision = revisions.Single(x => x.RequestId == id);
                if (revision.Origin != note.Origin || revision.VerificationLevel != note.VerificationLevel
                    || revision.AuthorServiceId != original.ServiceId || revision.AuthorUserId is not null || revision.SourceBatchId != context.BatchId
                    || revision.ClaimEpoch != original.ClaimEpoch || revision.SourceVersion != original.SourceVersion || revision.DeletionGeneration != original.DeletionGeneration
                    || revision.CreatedAtUtc != original.CommittedAtUtc || revision.EnvelopeSha256 != Convert.ToHexString(SHA256.HashData(revision.ProtectedContent))
                    || !resolved.TryGetValue(revision.ContentKeyId, out var key)) throw Unavailable();
                var clear = protector.Unprotect(new(scope, GroupBrainContentKind.RequestRevision, id, 1, original.SourceVersion,
                    original.DeletionGeneration), revision.ProtectedContent, key.Key, key.KeyId);
                if (!string.Equals(clear, payloads[index], StringComparison.Ordinal)) throw Unavailable();
                var refs = evidence.Where(x => x.RequestId == id).OrderBy(x => x.Ordinal).ToArray();
                if (refs.Length != note.Evidence.Count || refs.Where((x, ordinal) => x.Ordinal != ordinal + 1
                    || x.MessageId != note.Evidence[ordinal].MessageId || x.MessageRevision != note.Evidence[ordinal].Revision
                    || x.Kind != note.EvidenceKind).Any()) throw Unavailable();
            }
            var outboxes = await database.GroupNotesCommittedOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == context.BatchId
                && x.OperationId == operationId).Take(2).ToArrayAsync(cancellationToken);
            if (outboxes.Length != 1 || outboxes[0].Id != outboxId || outboxes[0].NoteCount != requestIds.Length
                || outboxes[0].IsHistoricalBackfill != historical || outboxes[0].CommittedAtUtc != original.CommittedAtUtc) throw Unavailable();
            var items = await database.GroupNotesCommittedItems.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OutboxId == outboxId)
                .OrderBy(x => x.Ordinal).Take(plan.MaximumNotes + 1).ToArrayAsync(cancellationToken);
            if (items.Length != requestIds.Length || items.Where((x, index) => x.Ordinal != index + 1
                || x.RequestId != requestIds[index] || x.RequestRevision != 1).Any()) throw Unavailable();
        }
        async Task<GroupSourceKeyMaterial> ResolveKeyAsync(string? id)
        {
            if (database.Database.CurrentTransaction is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
            Task<GroupSourceKeyMaterial>? pending = null;
            try
            {
                pending = (id is null ? keys.ResolveWriteAsync(scope, cancellationToken) : keys.ResolveReadAsync(scope, id, cancellationToken)).AsTask();
                return await pending.WaitAsync(cancellationToken) ?? throw Unavailable();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (pending is not null) _ = pending.ContinueWith(completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion) completed.Result?.Dispose(); else _ = completed.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            catch (Exception) { throw Unavailable(); }
        }
        GroupWorkRawDispositionRecord[] RawAccounting() => GroupWorkRawAccounting.Build(context,
            currentAllocation ?? throw Unavailable(), selected.ToDictionary(x => x.Key, x => (x.Value.Revision, x.Value.Outcome)), operationId);
        void DetachStaged() { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; }
        Guid Identity(string kind, int ordinal) => new(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-note-id-v1/{scope.TenantId:D}/{scope.CompanyId:D}/{scope.SourceBindingId:D}/{context.BatchId:D}/{operationId:D}/{kind}/{ordinal}"))).AsSpan(0, 16));
        GroupNoteCommitResult Result(GroupWorkCommitReceiptRecord value, bool previous) =>
            new(scope, value.BatchId, value.OperationId, outboxId, Array.AsReadOnly(requestIds), value.CommittedAtUtc, previous);
    }

    private void ValidateEntry(GroupGroundedWorkProposal proposal, GroupBrainPrivateContext dependencies, Guid operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); worker.Validate(); proposal.Scope.Validate();
        if (proposal.Scope.TenantId != worker.TenantId || proposal.Scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        var preparation = proposal.Preparation; var context = preparation.Context;
        if (operation == Guid.Empty || dependencies.Handle.Receipt != context.Handle.Receipt || proposal.Notes.Count is < 1 or > 20
            || context.HasCoverageGap || context.Items.Count is < 1 or > 100 || preparation.Candidates.Count != context.Items.Count
            || preparation.Receipts.Any(x => x.Disposition != GroupSourcePreparationDisposition.ModelText || x.HasUnsupportedMedia)
            || proposal.SourceDispositions.Count != context.Items.Count) throw Unavailable();
        if (!database.Database.IsSqlServer() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (ArgumentException) { throw Unavailable(); }
    }
    private void ValidateAutomaticEntry(GroupAutomaticNotePlan plan, GroupBrainPrivateContext dependencies, Guid operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); worker.Validate(); plan.Scope.Validate();
        if (plan.Scope.TenantId != worker.TenantId || plan.Scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        var context = plan.Preparation.Context;
        // Gaps need a durable coverage carrier before allowing this effect.
        // Zero-note/raw completion belongs to a separate reviewed consumer.
        if (operation == Guid.Empty || dependencies.Handle.Receipt != context.Handle.Receipt || plan.NoteCount is < 1 or > GroupAutomaticNotePlan.MaximumNotes
            || context.HasCoverageGap || context.Items.Count is < 1 or > 100 || plan.SourceDispositions.Count != context.Items.Count
            || plan.Preparation.Receipts.Count != context.Items.Count) throw Unavailable();
        if (!database.Database.IsSqlServer() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (ArgumentException) { throw Unavailable(); }
    }
    private DateTimeOffset UtcNow() { var now = clock.GetUtcNow(); return now.Offset == TimeSpan.Zero ? now : throw Unavailable(); }
    private static bool ValidKeyId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_');
    private static InvalidOperationException Unavailable() => new("Group note commit is unavailable.");
}
