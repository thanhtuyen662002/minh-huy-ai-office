using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupListenerCommittedReceipt(GroupListenerLeaseSnapshot Lease,
    bool Changed, bool CoverageRecorded, DateTimeOffset CommittedAtUtc, bool WasAlreadyCommitted);

public sealed class GroupListenerConflictException() : InvalidOperationException("Listener command conflicts with its committed identity.");

// Ownership commands require a sealed, separately authenticated service. The
// account lock precedes registry/lease reads and lasts through durable commit.
// A process OwnerId by itself can never enter this store.
public sealed class GroupListenerStore(PlatformDbContext database, GroupIngressRuntimePolicy policy, TimeProvider clock)
{
    public async Task<GroupListenerCommittedReceipt> ApplyAsync(VerifiedGroupListener verified, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (database.ChangeTracker.HasChanges() || database.Database.IsRelational() && !database.Database.IsSqlServer()) throw Unavailable();
        var account = verified.Account;
        var staged = new List<object>();
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        try
        {
            var permissions = new GroupIngressPermissionVerifier(database);
            var directory = new GroupServiceDirectory(database);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            // Order nonce then account: even conflicting reuse across accounts
            // cannot race two missing-receipt reads into a deadlock/extension.
            await LockAsync(FormattableString.Invariant($"aioffice:group-listener-command:{account.TenantId:N}/{account.CompanyId:N}/{verified.Service.ServiceId:N}/{verified.Service.CredentialEpoch}/{verified.Nonce:N}"), cancellationToken);
            await LockAsync($"aioffice:group-listener:{account.TenantId:N}/{account.CompanyId:N}/{account.ConnectorAccountId:N}", cancellationToken);
            await RequireAuthorityAsync(verified, directory, permissions, cancellationToken);
            var stored = await database.GroupListenerLeases.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == account.TenantId && x.CompanyId == account.CompanyId && x.ConnectorAccountId == account.ConnectorAccountId, cancellationToken);
            var now = clock.GetUtcNow().ToUniversalTime();
            var current = stored is null ? null : new GroupListenerLeaseSnapshot(account, stored.OwnerId, stored.Epoch, stored.HeartbeatAtUtc, stored.ExpiresAtUtc);
            var previous = await database.GroupListenerCommandReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == account.TenantId && x.CompanyId == account.CompanyId && x.ServiceId == verified.Service.ServiceId &&
                x.CredentialEpoch == verified.Service.CredentialEpoch && x.Nonce == verified.Nonce, cancellationToken);
            if (previous is not null)
            {
                await RequireAuthorityAsync(verified, directory, permissions, cancellationToken);
                var original = Reconcile(verified, previous, current, clock.GetUtcNow().ToUniversalTime());
                await transaction.CommitAsync(cancellationToken);
                return original;
            }
            var transition = GroupListenerLeasePolicy.Apply(account, verified.Command, current, now);
            GroupAccountCoverageGapRecord? coverage = null;
            if (transition.Changed)
            {
                foreach (var entry in database.ChangeTracker.Entries<GroupListenerLeaseRecord>().Where(x =>
                    x.Entity.TenantId == account.TenantId && x.Entity.CompanyId == account.CompanyId && x.Entity.ConnectorAccountId == account.ConnectorAccountId).ToArray())
                    entry.State = EntityState.Detached;
                if (stored is null)
                {
                    stored = new() { TenantId = account.TenantId, CompanyId = account.CompanyId, ConnectorAccountId = account.ConnectorAccountId };
                    database.Add(stored);
                }
                else database.Attach(stored);
                staged.Add(stored);
                stored.OwnerId = transition.Lease.OwnerId;
                stored.Epoch = transition.Lease.Epoch;
                stored.HeartbeatAtUtc = transition.Lease.HeartbeatAtUtc;
                stored.ExpiresAtUtc = transition.Lease.ExpiresAtUtc;
                if (transition.CoverageReason is not null)
                {
                    if (transition.CoverageOpenedAtUtc is not { } opened || opened > now) throw Unavailable();
                    coverage = new GroupAccountCoverageGapRecord
                    {
                        TenantId = account.TenantId,
                        CompanyId = account.CompanyId,
                        ConnectorAccountId = account.ConnectorAccountId,
                        ListenerEpoch = transition.Lease.Epoch,
                        Reason = transition.CoverageReason,
                        OpenedAtUtc = opened,
                        RecordedAtUtc = now
                    };
                }
                // Obtain the lease write lock before staging any account-gap
                // INSERT. A source read holds revision ranges before checking
                // account gaps; ingress may already hold a lease read lock.
                // Keep this lock order explicit instead of relying on EF's
                // ordering of unrelated inserts and updates in one batch.
                await database.SaveChangesAsync(cancellationToken);
            }
            await RequireAuthorityAsync(verified, directory, permissions, cancellationToken);
            var committedAt = clock.GetUtcNow().ToUniversalTime();
            if (committedAt < transition.Lease.HeartbeatAtUtc) throw Unavailable();
            // A delayed proof/write cannot return an already expired live ACK.
            // A Stop ACK reconciles a stopped row and conveys no live authority.
            if (verified.Command.Operation != GroupListenerOperation.Stop && transition.Lease.ExpiresAtUtc <= committedAt) throw GroupServiceDirectory.Denied();
            var receipt = new GroupListenerCommandReceiptRecord
            {
                TenantId = account.TenantId,
                CompanyId = account.CompanyId,
                ConnectorAccountId = account.ConnectorAccountId,
                ServiceId = verified.Service.ServiceId,
                CredentialEpoch = verified.Service.CredentialEpoch,
                Nonce = verified.Nonce,
                CommandSha256 = verified.CommandSha256,
                Operation = verified.Command.Operation,
                OwnerId = transition.Lease.OwnerId,
                ListenerEpoch = transition.Lease.Epoch,
                HeartbeatAtUtc = transition.Lease.HeartbeatAtUtc,
                ExpiresAtUtc = transition.Lease.ExpiresAtUtc,
                Changed = transition.Changed,
                CoverageRecorded = transition.CoverageReason is not null,
                CommittedAtUtc = committedAt
            };
            if (coverage is not null) { database.Add(coverage); staged.Add(coverage); }
            database.Add(receipt); staged.Add(receipt);
            await database.SaveChangesAsync(cancellationToken);
            await RequireAuthorityAsync(verified, directory, permissions, cancellationToken);
            var finalNow = clock.GetUtcNow().ToUniversalTime();
            if (finalNow < committedAt) throw Unavailable();
            if (verified.Command.Operation != GroupListenerOperation.Stop && transition.Lease.ExpiresAtUtc <= finalNow) throw GroupServiceDirectory.Denied();
            await transaction.CommitAsync(cancellationToken);
            return new(transition.Lease, transition.Changed, transition.CoverageReason is not null, committedAt, false);
        }
        catch (Exception error) when (error is DbUpdateException or SqlException or OverflowException)
        { DetachStaged(); throw Unavailable(); }
        catch { DetachStaged(); throw; }

        void DetachStaged() { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; }
    }

    private async Task RequireAuthorityAsync(VerifiedGroupListener verified, GroupServiceDirectory directory,
        GroupIngressPermissionVerifier permissions, CancellationToken cancellationToken)
    {
        var current = await directory.RequireCurrentAsync(verified.Service, cancellationToken);
        await permissions.RequireSafeRuntimeAsync(cancellationToken);
        var now = clock.GetUtcNow().ToUniversalTime();
        GroupServiceAuthenticator.RequireFreshSigningTime(verified.Service.SignedAtUtc, now);
        GroupServiceAuthenticator.RequireQualification(current.Account, GroupSourceEventKind.NewText, now, policy);
        if (current.Account.Id != verified.Account.ConnectorAccountId || current.Source.Scope.TenantId != verified.Account.TenantId ||
            current.Source.Scope.CompanyId != verified.Account.CompanyId) throw GroupServiceDirectory.Denied();
    }

    private static GroupListenerCommittedReceipt Reconcile(VerifiedGroupListener verified, GroupListenerCommandReceiptRecord receipt,
        GroupListenerLeaseSnapshot? current, DateTimeOffset now)
    {
        if (receipt.ConnectorAccountId != verified.Account.ConnectorAccountId || receipt.CommandSha256 != verified.CommandSha256)
            throw new GroupListenerConflictException();
        if (receipt.Operation != verified.Command.Operation || receipt.OwnerId != verified.Command.OwnerId || receipt.ListenerEpoch <= 0 ||
            verified.Command.Operation != GroupListenerOperation.Acquire && receipt.ListenerEpoch != verified.Command.ExpectedEpoch ||
            receipt.HeartbeatAtUtc.Offset != TimeSpan.Zero || receipt.ExpiresAtUtc.Offset != TimeSpan.Zero || receipt.CommittedAtUtc.Offset != TimeSpan.Zero ||
            receipt.ExpiresAtUtc < receipt.HeartbeatAtUtc || receipt.ExpiresAtUtc - receipt.HeartbeatAtUtc > GroupListenerLeasePolicy.LeaseDuration ||
            receipt.CommittedAtUtc < receipt.HeartbeatAtUtc || receipt.CommittedAtUtc > now ||
            receipt.CoverageRecorded && (!receipt.Changed || receipt.Operation == GroupListenerOperation.Renew)) throw Unavailable();
        if (current is null || current.Account != verified.Account || current.OwnerId != receipt.OwnerId || current.Epoch != receipt.ListenerEpoch ||
            current.HeartbeatAtUtc.Offset != TimeSpan.Zero || current.ExpiresAtUtc.Offset != TimeSpan.Zero || current.HeartbeatAtUtc > now ||
            current.ExpiresAtUtc < current.HeartbeatAtUtc || current.ExpiresAtUtc - current.HeartbeatAtUtc > GroupListenerLeasePolicy.LeaseDuration)
            throw GroupServiceDirectory.Denied();
        if (receipt.Operation == GroupListenerOperation.Stop)
        {
            if (receipt.ExpiresAtUtc != receipt.HeartbeatAtUtc || current.HeartbeatAtUtc != receipt.HeartbeatAtUtc || current.ExpiresAtUtc != receipt.ExpiresAtUtc)
                throw GroupServiceDirectory.Denied();
        }
        else if (receipt.ExpiresAtUtc <= now || current.ExpiresAtUtc <= now || receipt.CommittedAtUtc >= receipt.ExpiresAtUtc ||
            current.HeartbeatAtUtc < receipt.HeartbeatAtUtc || current.ExpiresAtUtc < receipt.ExpiresAtUtc) throw GroupServiceDirectory.Denied();
        return new(new(verified.Account, receipt.OwnerId, receipt.ListenerEpoch, receipt.HeartbeatAtUtc, receipt.ExpiresAtUtc),
            receipt.Changed, receipt.CoverageRecorded, receipt.CommittedAtUtc, true);
    }

    private async Task LockAsync(string resource, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlServer()) return;
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction() ?? throw Unavailable();
        command.CommandTimeout = 10;
        command.CommandText = "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000; SELECT @result;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.DbType = DbType.String;
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0) throw Unavailable();
    }

    private static InvalidOperationException Unavailable() => new("Group listener ownership is unavailable.");
}
