using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupIngressCommittedReceipt(GroupScope Source, Guid MessageId,
    long Revision, long CommittedSequence, DateTimeOffset CommittedAtUtc, bool WasAlreadyCommitted);

public sealed class GroupIngressConflictException() : InvalidOperationException("Group ingress event conflicts with its committed identity.");

// A connector ACK is created only after the immutable source, receipt, cursor
// and reference-only outbox have committed on the same pinned SQL transaction.
public sealed class GroupIngressStore(PlatformDbContext database, IGroupSourceKeyProvider keys,
    GroupSourceContentProtector protector, GroupIngressRuntimePolicy policy, TimeProvider clock)
{
    public async Task<GroupIngressCommittedReceipt> AcceptAsync(VerifiedGroupIngress verified, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (database.ChangeTracker.HasChanges() || (database.Database.IsRelational() && !database.Database.IsSqlServer())) throw Unavailable();
        var staged = new List<object>();
        var source = verified.Source;
        var payload = verified.Payload;
        var metadata = payload.Event;
        var now = clock.GetUtcNow().ToUniversalTime();
        var directory = new GroupServiceDirectory(database);
        var permissions = new GroupIngressPermissionVerifier(database);
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        try
        {
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            // Wait for prior source commits before reading registry/lease rows.
            // An external revoke completed while queued must win admission.
            await LockSourceAsync(source, cancellationToken);
            var authority = await directory.RequireCurrentAsync(verified.Service, cancellationToken);
            now = clock.GetUtcNow().ToUniversalTime();
            GroupServiceAuthenticator.RequireFreshSigningTime(verified.Service.SignedAtUtc, now);
            GroupServiceAuthenticator.RequireQualification(authority.Account, metadata.Kind, now, policy);
            await RequireListenerAsync(authority.Account.Id, verified, now, cancellationToken);
            var eventHash = GroupIngressIdentity.EventIndex(source, metadata.RevisionEventId);
            var envelopeHash = EnvelopeHash(metadata, payload.Text);
            var previous = await ReceiptAsync(source, eventHash, cancellationToken);
            if (previous is not null)
            {
                if (!string.Equals(previous.ExternalRevisionEventId, metadata.RevisionEventId, StringComparison.Ordinal) ||
                    previous.EnvelopeSha256 != envelopeHash) throw Conflict();
                var referencedMessage = await MessageAsync(source, GroupIngressIdentity.MessageIndex(source, metadata.MessageId), cancellationToken);
                if (referencedMessage is null || referencedMessage.Id != previous.MessageId ||
                    !string.Equals(referencedMessage.ExternalMessageId, metadata.MessageId, StringComparison.Ordinal)) throw Unavailable();
                var original = await database.GroupMessageRevisions.AsNoTracking().Where(x => x.TenantId == source.TenantId && x.CompanyId == source.CompanyId &&
                    x.BindingId == source.SourceBindingId && x.MessageId == previous.MessageId && x.Revision == previous.Revision)
                    .Select(x => new { x.CommittedSequence, x.CommittedAtUtc, x.ContentSha256, x.Kind, x.IsHistoricalBackfill }).SingleOrDefaultAsync(cancellationToken) ?? throw Unavailable();
                if (original.CommittedSequence <= 0 || original.CommittedAtUtc.Offset != TimeSpan.Zero || original.ContentSha256 != metadata.ContentSha256 ||
                    original.Kind != metadata.Kind || original.IsHistoricalBackfill != metadata.IsHistoricalBackfill) throw Unavailable();
                await RequireFinalAuthorityAsync(directory, permissions, verified, authority.Account.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(source, previous.MessageId, previous.Revision, original.CommittedSequence, original.CommittedAtUtc, true);
            }

            var message = await MessageAsync(source, GroupIngressIdentity.MessageIndex(source, metadata.MessageId), cancellationToken);
            if (message is not null && !string.Equals(message.ExternalMessageId, metadata.MessageId, StringComparison.Ordinal)) throw Conflict();
            var missingOriginal = message is null && (metadata.Kind is GroupSourceEventKind.Edit or GroupSourceEventKind.Recall);
            if (message is null)
            {
                message = new()
                {
                    TenantId = source.TenantId,
                    CompanyId = source.CompanyId,
                    BindingId = source.SourceBindingId,
                    Id = Guid.NewGuid(),
                    ExternalMessageId = metadata.MessageId,
                    IdentityHash = GroupIngressIdentity.MessageIndex(source, metadata.MessageId)
                };
                Add(message);
            }
            var lastRevision = await database.GroupMessageRevisions.AsNoTracking().Where(x => x.TenantId == source.TenantId && x.CompanyId == source.CompanyId &&
                x.BindingId == source.SourceBindingId && x.MessageId == message.Id).Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0;
            if ((metadata.Kind is GroupSourceEventKind.NewText or GroupSourceEventKind.Media) &&
                await database.GroupMessageRevisions.AsNoTracking().AnyAsync(x => x.TenantId == source.TenantId && x.CompanyId == source.CompanyId &&
                    x.BindingId == source.SourceBindingId && x.MessageId == message.Id && (x.Kind == GroupSourceEventKind.NewText || x.Kind == GroupSourceEventKind.Media), cancellationToken)) throw Conflict();
            if (metadata.Kind == GroupSourceEventKind.Recall && payload.Text.Length != 0) throw Conflict();

            // Re-read cursor after the transaction-owned lock; never reuse a
            // tracked snapshot or allocate an identity before another commit.
            var state = await database.GroupSourceStates.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == source.TenantId &&
                x.CompanyId == source.CompanyId && x.BindingId == source.SourceBindingId, cancellationToken);
            foreach (var tracked in database.ChangeTracker.Entries<GroupSourceStateRecord>().Where(x => x.Entity.TenantId == source.TenantId &&
                         x.Entity.CompanyId == source.CompanyId && x.Entity.BindingId == source.SourceBindingId).ToArray()) tracked.State = EntityState.Detached;
            if (state is null)
            {
                state = new() { TenantId = source.TenantId, CompanyId = source.CompanyId, BindingId = source.SourceBindingId };
                Add(state);
            }
            else { database.Attach(state); staged.Add(state); }
            var revision = checked(lastRevision + 1);
            var sequence = checked(state.CommittedSequence + 1);
            if (state.ScheduledThroughSequence < 0 || state.ScheduledThroughSequence > state.CommittedSequence ||
                (state.FirstPendingAtUtc is null) != (state.LastPendingAtUtc is null) || state.FirstPendingAtUtc > state.LastPendingAtUtc) throw Unavailable();
            var startsPendingWindow = state.FirstPendingAtUtc is null;

            using var key = await keys.ResolveWriteAsync(source, cancellationToken);
            var protectedContent = protector.Protect(new(source, message.Id, revision, verified.Service.SourceVersion, verified.Service.DeletionGeneration), payload.Text, key.Key, key.KeyId);
            await RequireFinalAuthorityAsync(directory, permissions, verified, authority.Account.Id, cancellationToken);
            // Source timestamps describe the admitted write, after key/proof
            // latency, rather than the earlier request admission time.
            now = clock.GetUtcNow().ToUniversalTime();
            Add(new GroupMessageRevisionRecord
            {
                TenantId = source.TenantId,
                CompanyId = source.CompanyId,
                BindingId = source.SourceBindingId,
                MessageId = message.Id,
                Revision = revision,
                CommittedSequence = sequence,
                ExternalRevisionEventId = metadata.RevisionEventId,
                SenderId = metadata.SenderId,
                ReplyToMessageId = metadata.ReplyToMessageId,
                Kind = metadata.Kind,
                ContentSha256 = metadata.ContentSha256,
                ContentKeyId = key.KeyId,
                ProtectedContent = protectedContent,
                SourceVersion = verified.Service.SourceVersion,
                DeletionGeneration = verified.Service.DeletionGeneration,
                OccurredAtUtc = metadata.OccurredAtUtc,
                CommittedAtUtc = now,
                IsHistoricalBackfill = metadata.IsHistoricalBackfill
            });
            Add(new GroupIngressReceiptRecord
            {
                TenantId = source.TenantId,
                CompanyId = source.CompanyId,
                BindingId = source.SourceBindingId,
                EventIdentityHash = eventHash,
                ExternalRevisionEventId = metadata.RevisionEventId,
                EnvelopeSha256 = envelopeHash,
                MessageId = message.Id,
                Revision = revision,
                ServiceId = verified.Service.ServiceId,
                CredentialEpoch = verified.Service.CredentialEpoch,
                ListenerEpoch = payload.ListenerEpoch,
                CommittedAtUtc = now
            });
            Add(new GroupIngressOutboxRecord
            {
                TenantId = source.TenantId,
                CompanyId = source.CompanyId,
                BindingId = source.SourceBindingId,
                Id = Guid.NewGuid(),
                MessageId = message.Id,
                Revision = revision,
                CommittedSequence = sequence,
                AvailableAtUtc = now
            });
            if (missingOriginal) Add(new GroupCoverageGapRecord
            {
                TenantId = source.TenantId,
                CompanyId = source.CompanyId,
                BindingId = source.SourceBindingId,
                Id = Guid.NewGuid(),
                AfterCommittedSequence = state.CommittedSequence,
                Reason = "original-message-unseen",
                OpenedAtUtc = now
            });
            state.CommittedSequence = sequence;
            state.FirstPendingAtUtc ??= now;
            state.LastPendingAtUtc = state.LastPendingAtUtc > now ? state.LastPendingAtUtc : now;
            await database.SaveChangesAsync(cancellationToken);
            await RequireFinalAuthorityAsync(directory, permissions, verified, authority.Account.Id, cancellationToken);
            // The mutable scheduling anchor also accounts for source-write and
            // final-proof latency. Immutable source receipts retain write time.
            var pendingAt = clock.GetUtcNow().ToUniversalTime();
            if (state.LastPendingAtUtc < pendingAt)
            {
                state.LastPendingAtUtc = pendingAt;
                if (startsPendingWindow) state.FirstPendingAtUtc = pendingAt;
                await database.SaveChangesAsync(cancellationToken);
                await RequireFinalAuthorityAsync(directory, permissions, verified, authority.Account.Id, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new(source, message.Id, revision, sequence, now, false);
        }
        catch (Exception error) when (error is DbUpdateException or SqlException or OverflowException)
        { DetachStaged(); throw Unavailable(); }
        catch { DetachStaged(); throw; }

        void Add(object entity) { database.Add(entity); staged.Add(entity); }
        void DetachStaged() { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; }
    }

    private async Task RequireFinalAuthorityAsync(GroupServiceDirectory directory, GroupIngressPermissionVerifier permissions,
        VerifiedGroupIngress verified, Guid account, CancellationToken cancellationToken)
    {
        var current = await directory.RequireCurrentAsync(verified.Service, cancellationToken);
        await permissions.RequireSafeRuntimeAsync(cancellationToken);
        var now = clock.GetUtcNow().ToUniversalTime();
        GroupServiceAuthenticator.RequireFreshSigningTime(verified.Service.SignedAtUtc, now);
        GroupServiceAuthenticator.RequireQualification(current.Account, verified.Payload.Event.Kind, now, policy);
        await RequireListenerAsync(account, verified, now, cancellationToken);
        var finalNow = clock.GetUtcNow().ToUniversalTime();
        GroupServiceAuthenticator.RequireFreshSigningTime(verified.Service.SignedAtUtc, finalNow);
        GroupServiceAuthenticator.RequireQualification(current.Account, verified.Payload.Event.Kind, finalNow, policy);
    }

    private async Task RequireListenerAsync(Guid account, VerifiedGroupIngress verified, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var source = verified.Source;
        var lease = await database.GroupListenerLeases.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == source.TenantId && x.CompanyId == source.CompanyId &&
            x.ConnectorAccountId == account, cancellationToken);
        now = clock.GetUtcNow().ToUniversalTime();
        if (lease is null || lease.OwnerId != verified.Payload.ListenerOwnerId || lease.Epoch != verified.Payload.ListenerEpoch ||
            lease.ExpiresAtUtc <= now || lease.HeartbeatAtUtc > now || lease.HeartbeatAtUtc.Offset != TimeSpan.Zero || lease.ExpiresAtUtc.Offset != TimeSpan.Zero)
            throw GroupServiceDirectory.Denied();
    }

    private async Task LockSourceAsync(GroupScope source, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlServer()) return;
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction() ?? throw Unavailable();
        command.CommandTimeout = 10;
        command.CommandText = "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000; SELECT @result;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.DbType = DbType.String;
        parameter.Value = $"aioffice:group-ingest:{source.TenantId:N}/{source.CompanyId:N}/{source.SourceBindingId:N}"; command.Parameters.Add(parameter);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0) throw Unavailable();
    }

    private async Task<GroupMessageRecord?> MessageAsync(GroupScope source, string hash, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlServer()) return await database.GroupMessages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == source.TenantId &&
            x.CompanyId == source.CompanyId && x.BindingId == source.SourceBindingId && x.IdentityHash == hash, cancellationToken);
        var row = await database.Database.SqlQuery<StoredMessage>($"""
            SELECT Id,IdentityHash,CASE WHEN DATALENGTH(ExternalMessageId)<=512 THEN CONVERT(varbinary(512),ExternalMessageId) END AS MessageBytes
            FROM aioffice.GroupMessages WHERE TenantId={source.TenantId} AND CompanyId={source.CompanyId} AND BindingId={source.SourceBindingId} AND IdentityHash={hash}
            """).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new()
        {
            TenantId = source.TenantId,
            CompanyId = source.CompanyId,
            BindingId = source.SourceBindingId,
            Id = row.Id,
            IdentityHash = row.IdentityHash,
            ExternalMessageId = GroupRegistryReader.Decode(row.MessageBytes, 512)
        };
    }

    private async Task<GroupIngressReceiptRecord?> ReceiptAsync(GroupScope source, string hash, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlServer()) return await database.GroupIngressReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == source.TenantId &&
            x.CompanyId == source.CompanyId && x.BindingId == source.SourceBindingId && x.EventIdentityHash == hash, cancellationToken);
        var row = await database.Database.SqlQuery<StoredReceipt>($"""
            SELECT MessageId,Revision,EnvelopeSha256,CASE WHEN DATALENGTH(ExternalRevisionEventId)<=512 THEN CONVERT(varbinary(512),ExternalRevisionEventId) END AS EventBytes
            FROM aioffice.GroupIngressReceipts WHERE TenantId={source.TenantId} AND CompanyId={source.CompanyId} AND BindingId={source.SourceBindingId} AND EventIdentityHash={hash}
            """).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new()
        {
            MessageId = row.MessageId,
            Revision = row.Revision,
            EnvelopeSha256 = row.EnvelopeSha256,
            ExternalRevisionEventId = GroupRegistryReader.Decode(row.EventBytes, 512)
        };
    }

    internal static string EnvelopeHash(GroupSourceEventMetadata metadata, string text)
    {
        // Exclude transport listener epoch/owner and HMAC nonce/time so a
        // durable spool can replay the same logical event after a restart.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LogicalEnvelope("aioffice-group-logical-event-v1", metadata, text), GroupServiceAuthenticator.JsonOptions);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private sealed record LogicalEnvelope(string Domain, GroupSourceEventMetadata Event, string Text);
    private sealed record StoredMessage(Guid Id, string IdentityHash, byte[]? MessageBytes);
    private sealed record StoredReceipt(Guid MessageId, long Revision, string EnvelopeSha256, byte[]? EventBytes);
    private static InvalidOperationException Unavailable() => new("Group ingress is unavailable.");
    private static GroupIngressConflictException Conflict() => new();
}
