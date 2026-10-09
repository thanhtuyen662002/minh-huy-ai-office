using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupSourceMessageView(GroupScope Source, Guid MessageId, string ExternalMessageId,
    long Revision, long CommittedSequence, GroupSourceEventKind Kind, string SenderId, string? ReplyToMessageId,
    DateTimeOffset OccurredAtUtc, string? Text, bool IsHistoricalBackfill, bool HasCoverageGap);

// Read grants are separate from service Ingest/Extract/Notify and IT disclosure.
// No portal role (including administrator) substitutes for an explicit source grant.
public sealed class GroupSourceReader(PlatformDbContext database, IAuthorizationDirectory directory,
    IGroupSourceKeyProvider keys, GroupSourceContentProtector protector)
{
    public async Task<GroupSourceMessageView?> GetAsync(AuthorizationContext authority, Guid sourceId, Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (sourceId == Guid.Empty || messageId == Guid.Empty) throw new ArgumentException("Source message identity is required.");
        if (database.ChangeTracker.HasChanges() || database.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            throw Unavailable();
        var relational = database.Database.IsRelational();
        if (relational && !database.Database.IsSqlServer()) throw Unavailable();
        var opened = relational && database.Database.GetDbConnection().State != ConnectionState.Open;
        try
        {
            // Pin permission, registry and private reads to the same SQL identity.
            // Do not hold Serializable membership/grant locks across key awaits:
            // current external revocation must remain observable at final release.
            if (opened) await database.Database.OpenConnectionAsync(cancellationToken);
            if (relational)
            {
                await using var command = database.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id=@@SPID;";
                command.CommandTimeout = 5;
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 2) throw Unavailable();
            }
            var before = await RequireAccessAsync(authority, sourceId, cancellationToken);
            var source = before.Binding;
            var scope = new GroupScope(authority.TenantId, authority.CompanyId, sourceId);
            var message = await MessageAsync(scope, messageId, cancellationToken);
            if (message is null)
            {
                await RequireUnchangedAccessAsync(authority, sourceId, before, cancellationToken);
                return null;
            }
            var externalMessage = Text(message.ExternalBytes, message.ExternalText);
            if (message.IdentityHash != GroupIngressIdentity.MessageIndex(scope, externalMessage)) throw Unavailable();
            var revision = await WinnerAsync(scope, messageId, cancellationToken) ?? throw Unavailable();
            ValidateRevision(revision, before);
            var eventId = Text(revision.EventBytes, revision.EventText);
            var sender = Text(revision.SenderBytes, revision.SenderText);
            var reply = Reply(revision);
            var receipt = await ReceiptAsync(scope, eventId, cancellationToken) ?? throw Unavailable();
            if (receipt.MessageId != messageId || receipt.Revision != revision.Revision || Text(receipt.EventBytes, receipt.EventText) != eventId) throw Unavailable();
            await RequireUnchangedAccessAsync(authority, sourceId, before, cancellationToken);
            using var key = await keys.ResolveReadAsync(scope, revision.ContentKeyId, cancellationToken);
            var text = protector.Unprotect(new(scope, messageId, revision.Revision, revision.SourceVersion, revision.DeletionGeneration),
                revision.ProtectedContent ?? throw Unavailable(), key.Key, key.KeyId);
            var metadata = new GroupSourceEventMetadata(new(source.Provider, source.ExternalAccountId, source.ExternalGroupId), externalMessage,
                eventId, sender, reply, revision.Kind, revision.OccurredAtUtc, revision.ContentSha256, revision.IsHistoricalBackfill);
            metadata.Validate();
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) != revision.ContentSha256 ||
                GroupIngressStore.EnvelopeHash(metadata, text) != receipt.EnvelopeSha256 || revision.Kind == GroupSourceEventKind.Recall && text.Length != 0)
                throw Unavailable();
            // Resolve no private key while holding registry/member/revision locks.
            // The short final transaction is the read's linearization boundary:
            // authority is checked before the current winner, and both remain
            // locked through commit. A recall during the final directory await
            // must be observed before any private body can be released.
            await using var release = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            await RequireUnchangedAccessAsync(authority, sourceId, before, cancellationToken);
            var final = await WinnerAsync(scope, messageId, cancellationToken) ?? throw Unavailable();
            if (final.Revision != revision.Revision || final.CommittedSequence != revision.CommittedSequence || final.Kind != revision.Kind ||
                final.ContentSha256 != revision.ContentSha256 || final.ProtectedContent is null ||
                !final.ProtectedContent.AsSpan().SequenceEqual(revision.ProtectedContent) || final.ContentKeyId != revision.ContentKeyId ||
                final.SourceVersion != revision.SourceVersion || final.DeletionGeneration != revision.DeletionGeneration ||
                final.OccurredAtUtc != revision.OccurredAtUtc || final.IsHistoricalBackfill != revision.IsHistoricalBackfill ||
                Text(final.EventBytes, final.EventText) != eventId || Text(final.SenderBytes, final.SenderText) != sender || Reply(final) != reply) throw Unavailable();
            var finalMessage = await MessageAsync(scope, messageId, cancellationToken) ?? throw Unavailable();
            if (finalMessage.IdentityHash != message.IdentityHash || Text(finalMessage.ExternalBytes, finalMessage.ExternalText) != externalMessage) throw Unavailable();
            var finalReceipt = await ReceiptAsync(scope, eventId, cancellationToken) ?? throw Unavailable();
            if (finalReceipt.EnvelopeSha256 != receipt.EnvelopeSha256 || finalReceipt.MessageId != messageId || finalReceipt.Revision != revision.Revision ||
                Text(finalReceipt.EventBytes, finalReceipt.EventText) != eventId) throw Unavailable();
            var gap = await database.GroupCoverageGaps.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId &&
                x.BindingId == sourceId, cancellationToken);
            await release.CommitAsync(cancellationToken);
            return new(scope, messageId, externalMessage, revision.Revision, revision.CommittedSequence, revision.Kind, sender, reply,
                revision.OccurredAtUtc, revision.Kind == GroupSourceEventKind.Recall ? null : text, revision.IsHistoricalBackfill, gap);
        }
        finally { if (opened) await database.Database.CloseConnectionAsync(); }
    }

    private async Task<Access> RequireAccessAsync(AuthorizationContext authority, Guid source, CancellationToken cancellationToken)
    {
        if ((await directory.ResolveAsync(authority, cancellationToken))?.Context != authority) throw GroupServiceDirectory.Denied();
        await new GroupIngressPermissionVerifier(database).RequireSafeRuntimeAsync(cancellationToken);
        var grant = await database.GroupReaderGrants.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == authority.TenantId &&
            x.CompanyId == authority.CompanyId && x.UserId == authority.UserId && x.BindingId == source, cancellationToken);
        if (grant is not { IsEnabled: true, Version: > 0 }) throw GroupServiceDirectory.Denied();
        var hash = await database.GroupBindings.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId &&
            x.Id == source).Select(x => x.IdentityHash).SingleOrDefaultAsync(cancellationToken);
        if (hash is null) throw GroupServiceDirectory.Denied();
        var binding = await GroupRegistryReader.BindingAsync(database, hash, cancellationToken);
        if (binding is not { IsEnabled: true, Role: GroupBindingRole.CustomerSource, Version: > 0, DeletionGeneration: >= 0 } ||
            binding.TenantId != authority.TenantId || binding.CompanyId != authority.CompanyId || binding.Id != source ||
            !GroupIngressIdentity.Matches(binding, new(binding.Provider, binding.ExternalAccountId, binding.ExternalGroupId)) ||
            !await GroupRegistryReader.IsExclusivePhysicalOwnerAsync(database, binding, cancellationToken)) throw GroupServiceDirectory.Denied();
        return new(binding, grant.Version);
    }

    private async Task RequireUnchangedAccessAsync(AuthorizationContext authority, Guid source, Access before, CancellationToken cancellationToken)
    {
        var current = await RequireAccessAsync(authority, source, cancellationToken);
        if (current.GrantVersion != before.GrantVersion || current.Binding.Version != before.Binding.Version ||
            current.Binding.DeletionGeneration != before.Binding.DeletionGeneration || current.Binding.IdentityHash != before.Binding.IdentityHash ||
            current.Binding.ConnectorAccountId != before.Binding.ConnectorAccountId || current.Binding.Provider != before.Binding.Provider ||
            current.Binding.ExternalAccountId != before.Binding.ExternalAccountId || current.Binding.ExternalGroupId != before.Binding.ExternalGroupId)
            throw GroupServiceDirectory.Denied();
    }

    private async Task<StoredMessage?> MessageAsync(GroupScope source, Guid message, CancellationToken cancellationToken)
    {
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<StoredMessage>($"""
            SELECT TenantId,CompanyId,BindingId,Id,IdentityHash,
              CASE WHEN DATALENGTH(ExternalMessageId)<=512 THEN CONVERT(varbinary(512),ExternalMessageId) END AS ExternalBytes,
              CAST(NULL AS nvarchar(256)) AS ExternalText FROM aioffice.GroupMessages
            """) : database.GroupMessages.AsNoTracking().Select(x => new StoredMessage(x.TenantId, x.CompanyId, x.BindingId, x.Id, x.IdentityHash, null, x.ExternalMessageId));
        return await rows.SingleOrDefaultAsync(x => x.TenantId == source.TenantId && x.CompanyId == source.CompanyId && x.BindingId == source.SourceBindingId && x.Id == message, cancellationToken);
    }

    private async Task<StoredRevision?> WinnerAsync(GroupScope source, Guid message, CancellationToken cancellationToken)
    {
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<StoredRevision>($"""
            SELECT TenantId,CompanyId,BindingId,MessageId,Revision,CommittedSequence,Kind,ContentSha256,ContentKeyId,SourceVersion,DeletionGeneration,OccurredAtUtc,IsHistoricalBackfill,
              CASE WHEN DATALENGTH(ProtectedContent)<=65536 THEN ProtectedContent END AS ProtectedContent,
              CASE WHEN DATALENGTH(ExternalRevisionEventId)<=512 THEN CONVERT(varbinary(512),ExternalRevisionEventId) END AS EventBytes,
              CASE WHEN DATALENGTH(SenderId)<=512 THEN CONVERT(varbinary(512),SenderId) END AS SenderBytes,
              CASE WHEN DATALENGTH(ReplyToMessageId)<=512 THEN CONVERT(varbinary(512),ReplyToMessageId) END AS ReplyBytes,
              CAST(CASE WHEN ReplyToMessageId IS NULL THEN 1 ELSE 0 END AS bit) AS ReplyIsNull,
              CAST(NULL AS nvarchar(256)) AS EventText,CAST(NULL AS nvarchar(256)) AS SenderText,CAST(NULL AS nvarchar(256)) AS ReplyText
            FROM aioffice.GroupMessageRevisions
            """) : database.GroupMessageRevisions.AsNoTracking().Select(x => new StoredRevision(x.TenantId, x.CompanyId, x.BindingId, x.MessageId,
                x.Revision, x.CommittedSequence, x.Kind, x.ContentSha256, x.ContentKeyId, x.SourceVersion, x.DeletionGeneration, x.OccurredAtUtc,
                x.IsHistoricalBackfill, x.ProtectedContent, null, null, null, x.ReplyToMessageId == null, x.ExternalRevisionEventId, x.SenderId, x.ReplyToMessageId));
        return await rows.Where(x => x.TenantId == source.TenantId && x.CompanyId == source.CompanyId && x.BindingId == source.SourceBindingId && x.MessageId == message)
            .OrderByDescending(x => x.Kind == GroupSourceEventKind.Recall ? 3 : x.Kind == GroupSourceEventKind.Edit ? 2 : 1)
            .ThenByDescending(x => x.Revision).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<StoredReceipt?> ReceiptAsync(GroupScope source, string eventId, CancellationToken cancellationToken)
    {
        var hash = GroupIngressIdentity.EventIndex(source, eventId);
        var rows = database.Database.IsSqlServer() ? database.Database.SqlQuery<StoredReceipt>($"""
            SELECT TenantId,CompanyId,BindingId,EventIdentityHash,MessageId,Revision,EnvelopeSha256,
              CASE WHEN DATALENGTH(ExternalRevisionEventId)<=512 THEN CONVERT(varbinary(512),ExternalRevisionEventId) END AS EventBytes,
              CAST(NULL AS nvarchar(256)) AS EventText FROM aioffice.GroupIngressReceipts
            """) : database.GroupIngressReceipts.AsNoTracking().Select(x => new StoredReceipt(x.TenantId, x.CompanyId, x.BindingId, x.EventIdentityHash,
                x.MessageId, x.Revision, x.EnvelopeSha256, null, x.ExternalRevisionEventId));
        return await rows.SingleOrDefaultAsync(x => x.TenantId == source.TenantId && x.CompanyId == source.CompanyId && x.BindingId == source.SourceBindingId && x.EventIdentityHash == hash, cancellationToken);
    }

    private static string Text(byte[]? bytes, string? text)
    {
        var value = bytes is null ? text ?? throw Unavailable() : GroupRegistryReader.Decode(bytes, 512);
        GroupExternalIdentity.ValidateOpaqueId(value);
        return value;
    }
    private static string? Reply(StoredRevision row)
    {
        if (!row.ReplyIsNull) return Text(row.ReplyBytes, row.ReplyText);
        if (row.ReplyBytes is not null || row.ReplyText is not null) throw Unavailable();
        return null;
    }
    private static void ValidateRevision(StoredRevision row, Access access)
    {
        if (row.Revision <= 0 || row.CommittedSequence <= 0 || !Enum.IsDefined(row.Kind) || row.SourceVersion != access.Binding.Version ||
            row.DeletionGeneration != access.Binding.DeletionGeneration || row.OccurredAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
    }
    private static InvalidOperationException Unavailable() => new("Group source content is unavailable.");
    private sealed record Access(GroupBindingRecord Binding, long GrantVersion);
    private sealed record StoredMessage(Guid TenantId, Guid CompanyId, Guid BindingId, Guid Id, string IdentityHash, byte[]? ExternalBytes, string? ExternalText);
    private sealed record StoredReceipt(Guid TenantId, Guid CompanyId, Guid BindingId, string EventIdentityHash, Guid MessageId, long Revision, string EnvelopeSha256, byte[]? EventBytes, string? EventText);
    private sealed record StoredRevision(Guid TenantId, Guid CompanyId, Guid BindingId, Guid MessageId, long Revision, long CommittedSequence,
        GroupSourceEventKind Kind, string ContentSha256, string ContentKeyId, long SourceVersion, long DeletionGeneration,
        DateTimeOffset OccurredAtUtc, bool IsHistoricalBackfill, byte[]? ProtectedContent, byte[]? EventBytes, byte[]? SenderBytes,
        byte[]? ReplyBytes, bool ReplyIsNull, string? EventText, string? SenderText, string? ReplyText);
}
