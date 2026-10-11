using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class GroupBatchClaimCommitException() : InvalidOperationException("Group batch claim commit is not available.");

// Metadata leases only. A handle must be fenced again inside the transaction
// that reads protected context or commits a result. Acquisition is not completion.
public sealed class GroupBatchClaimStore(PlatformDbContext database, GroupExtractionWorkerBinding worker, TimeProvider clock)
{
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);

    public async Task<GroupBatchClaimResult?> TryAcquireAsync(GroupScope scope, Guid batchId, Guid ownerId,
        Guid operationId, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope, cancellationToken);
        if (batchId == Guid.Empty || ownerId == Guid.Empty || operationId == Guid.Empty
            || lifetime < MinimumLifetime || lifetime > MaximumLifetime) throw Unavailable();
        ValidateEntry(scope, cancellationToken);
        var staged = new List<object>();
        var directory = new GroupExtractionDirectory(database, worker);
        var permissions = new GroupIngressPermissionVerifier(database);
        try
        {
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await GroupSourceTransactionLock.RequireAsync(database, scope, cancellationToken);
            var authority = await directory.RequireAsync(scope, cancellationToken);
            var now = UtcNow();
            var original = await Receipts(scope).SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
            if (original is not null)
            {
                ValidateReceipt(original, scope, now);
                if (original.BatchId != batchId || original.OwnerId != ownerId || original.RequestedLifetimeTicks != lifetime.Ticks)
                    throw Unavailable();
                await ReadAllocationAsync(scope, batchId, now, cancellationToken);
                var state = await ReadStateAsync(scope, batchId, now, cancellationToken) ?? throw Unavailable();
                var finalNow = await FinalAuthorityAsync(allowExpiredClockRollback:
                    state.ExpiryObservedAtUtc is not null || now >= state.ExpiresAtUtc);
                if (Matches(state, original))
                    await PersistExpiryAsync(state, finalNow > now ? finalNow : now, cancellationToken);
                await directory.RequireCurrentAsync(authority, cancellationToken);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                var receipt = Receipt(scope, original);
                return await FinishAsync(new(receipt, IsCurrent(state, original, authority, finalNow)
                    ? new GroupBatchClaimHandle(receipt, authority) : null, true));
            }
            await ReadAllocationAsync(scope, batchId, now, cancellationToken);
            var current = await ReadStateAsync(scope, batchId, now, cancellationToken);
            if (current is not null && (current.ExpiresAtUtc > now || current.ExpiryObservedAtUtc > now))
            {
                await FinalAuthorityAsync(); await transaction.CommitAsync(cancellationToken); return null;
            }
            var record = new GroupBatchClaimReceiptRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                OperationId = operationId,
                BatchId = batchId,
                OwnerId = ownerId,
                Epoch = current is null ? 1 : checked(current.Epoch + 1),
                RequestedLifetimeTicks = lifetime.Ticks,
                IssuedAtUtc = now,
                ExpiresAtUtc = now.Add(lifetime),
                ServiceId = worker.ServiceId,
                CredentialEpoch = worker.CredentialEpoch,
                GrantVersion = authority.Grant.Version,
                SourceVersion = authority.Source.Version,
                DeletionGeneration = authority.Source.DeletionGeneration,
                AccountVersion = authority.AccountVersion,
                AuthoritySha256 = AuthorityFingerprint(authority)
            };
            ValidateReceipt(record, scope, now);
            if (current is null)
            {
                current = new()
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    BatchId = batchId
                };
                database.Add(current);
            }
            else
            {
                foreach (var tracked in database.ChangeTracker.Entries<GroupBatchClaimStateRecord>().Where(x =>
                    x.Entity.TenantId == scope.TenantId && x.Entity.CompanyId == scope.CompanyId
                    && x.Entity.BindingId == scope.SourceBindingId && x.Entity.BatchId == batchId).ToArray())
                    tracked.State = EntityState.Detached;
                database.Attach(current);
            }
            staged.Add(current); database.Add(record); staged.Add(record);
            current.Epoch = record.Epoch; current.OwnerId = ownerId; current.OperationId = operationId;
            current.IssuedAtUtc = record.IssuedAtUtc; current.ExpiresAtUtc = record.ExpiresAtUtc;
            current.ExpiryObservedAtUtc = null;
            await FinalAuthorityAsync(record.ExpiresAtUtc);
            await database.SaveChangesAsync(cancellationToken);
            await FinalAuthorityAsync(record.ExpiresAtUtc);
            var committedReceipt = Receipt(scope, record);
            return await FinishAsync(new(committedReceipt, new(committedReceipt, authority), false));

            async Task<DateTimeOffset> FinalAuthorityAsync(DateTimeOffset? requiredExpiry = null, bool allowExpiredClockRollback = false)
            {
                await directory.RequireCurrentAsync(authority, cancellationToken);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                var finalNow = UtcNow();
                if ((!allowExpiredClockRollback && finalNow < now) || (requiredExpiry is not null && finalNow >= requiredExpiry)) throw Unavailable();
                return finalNow;
            }
            async Task<GroupBatchClaimResult> FinishAsync(GroupBatchClaimResult result)
            {
                await transaction.CommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
        }
        catch (DbUpdateException) { throw new GroupBatchClaimCommitException(); }
        catch (Exception error) when (error is SqlException or OverflowException or ArgumentOutOfRangeException or EncoderFallbackException)
        { throw Unavailable(); }
        finally { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; }
    }

    // This public check establishes only current metadata at this instant.
    // Expiry retirement commits in this metadata-only unit before denial. The
    // internal verdict requires an effect consumer to own rollback/retirement;
    // it must never commit staged effects just to preserve an expiry witness.
    public async Task RequireCurrentAsync(GroupBatchClaimHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ValidateEntry(handle.Receipt.Scope, cancellationToken);
        try
        {
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            var verdict = await InspectCurrentLockedAsync(handle, cancellationToken);
            if (verdict is GroupBatchClaimFenceVerdict.Expired expired)
                await RetireExpiredLockedAsync(expired.Observation, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (verdict is GroupBatchClaimFenceVerdict.Expired) throw GroupServiceDirectory.Denied();
        }
        catch (DbUpdateException) { throw new GroupBatchClaimCommitException(); }
        catch (Exception error) when (error is SqlException or OverflowException or ArgumentOutOfRangeException or EncoderFallbackException)
        { throw Unavailable(); }
    }

    internal async Task<GroupBatchClaimFenceVerdict> InspectCurrentLockedAsync(GroupBatchClaimHandle handle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ValidateScope(handle.Receipt.Scope, cancellationToken);
        if (database.Database.IsSqlServer() && database.Database.CurrentTransaction is null) throw Unavailable();
        var scope = handle.Receipt.Scope;
        var permissions = new GroupIngressPermissionVerifier(database);
        var directory = new GroupExtractionDirectory(database, worker);
        await permissions.RequireSafeRuntimeAsync(cancellationToken);
        await GroupSourceTransactionLock.RequireAsync(database, scope, cancellationToken);
        await directory.RequireCurrentAsync(handle.Authority, cancellationToken);
        var now = UtcNow();
        var original = await Receipts(scope).SingleOrDefaultAsync(x => x.OperationId == handle.Receipt.OperationId, cancellationToken)
            ?? throw Unavailable();
        ValidateReceipt(original, scope, now);
        if (Receipt(scope, original) != handle.Receipt) throw GroupServiceDirectory.Denied();
        var state = await ReadStateAsync(scope, original.BatchId, now, cancellationToken) ?? throw Unavailable();
        if (!Matches(state, original) || !MatchesAuthority(original, handle.Authority)) throw GroupServiceDirectory.Denied();
        var allocation = await ReadAllocationAsync(scope, original.BatchId, now, cancellationToken);
        await directory.RequireCurrentAsync(handle.Authority, cancellationToken);
        await permissions.RequireSafeRuntimeAsync(cancellationToken);
        var finalNow = UtcNow();
        var observedAt = finalNow > now ? finalNow : now;
        if (state.ExpiryObservedAtUtc is not null || observedAt >= state.ExpiresAtUtc)
            return new GroupBatchClaimFenceVerdict.Expired(new(handle, state.ExpiryObservedAtUtc ?? observedAt));
        if (finalNow < now) throw GroupServiceDirectory.Denied();
        return new GroupBatchClaimFenceVerdict.Current(allocation);
    }

    // Caller must have rolled back ALL effect writes and detached their EF
    // entries before this operation. It does not commit the caller's unit.
    // The first effect consumer needs an owned savepoint/transaction wrapper
    // and native rollback proof; no such consumer is shipped at this checkpoint.
    internal async Task RetireExpiredLockedAsync(GroupBatchClaimExpiryObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var handle = observation.Handle; var scope = handle.Receipt.Scope;
        ValidateEntry(scope, cancellationToken);
        if (database.Database.IsSqlServer() && database.Database.CurrentTransaction is null) throw Unavailable();
        if (observation.ObservedAtUtc.Offset != TimeSpan.Zero || observation.ObservedAtUtc < handle.Receipt.ExpiresAtUtc)
            throw Unavailable();
        var permissions = new GroupIngressPermissionVerifier(database);
        var directory = new GroupExtractionDirectory(database, worker);
        await permissions.RequireSafeRuntimeAsync(cancellationToken);
        await GroupSourceTransactionLock.RequireAsync(database, scope, cancellationToken);
        await directory.RequireCurrentAsync(handle.Authority, cancellationToken);
        var now = UtcNow(); var validationTime = now > observation.ObservedAtUtc ? now : observation.ObservedAtUtc;
        var original = await Receipts(scope).SingleOrDefaultAsync(x => x.OperationId == handle.Receipt.OperationId, cancellationToken)
            ?? throw Unavailable();
        ValidateReceipt(original, scope, validationTime);
        var state = await ReadStateAsync(scope, original.BatchId, validationTime, cancellationToken) ?? throw Unavailable();
        if (Receipt(scope, original) != handle.Receipt || !Matches(state, original) || !MatchesAuthority(original, handle.Authority))
            throw GroupServiceDirectory.Denied();
        await ReadAllocationAsync(scope, original.BatchId, validationTime, cancellationToken);
        await directory.RequireCurrentAsync(handle.Authority, cancellationToken);
        await permissions.RequireSafeRuntimeAsync(cancellationToken);
        await PersistExpiryAsync(state, observation.ObservedAtUtc, cancellationToken);
        await directory.RequireCurrentAsync(handle.Authority, cancellationToken);
        await permissions.RequireSafeRuntimeAsync(cancellationToken);
    }

    private async Task PersistExpiryAsync(GroupBatchClaimStateRecord state, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        if (state.ExpiryObservedAtUtc is not null || observedAtUtc < state.ExpiresAtUtc) return;
        if (observedAtUtc.Offset != TimeSpan.Zero || database.ChangeTracker.HasChanges()) throw Unavailable();
        foreach (var tracked in database.ChangeTracker.Entries<GroupBatchClaimStateRecord>().Where(x =>
            x.Entity.TenantId == state.TenantId && x.Entity.CompanyId == state.CompanyId
            && x.Entity.BindingId == state.BindingId && x.Entity.BatchId == state.BatchId).ToArray())
            tracked.State = EntityState.Detached;
        database.Attach(state);
        try
        {
            state.ExpiryObservedAtUtc = observedAtUtc;
            await database.SaveChangesAsync(cancellationToken);
        }
        finally { database.Entry(state).State = EntityState.Detached; }
    }

    private async Task<GroupBatchClaimStateRecord?> ReadStateAsync(GroupScope scope, Guid batchId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await database.GroupBatchClaimStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.BatchId == batchId, cancellationToken);
        if (state is null)
        {
            if (await Receipts(scope).AnyAsync(x => x.BatchId == batchId, cancellationToken)) throw Unavailable();
            return null;
        }
        if (state.Epoch <= 0 || state.OwnerId == Guid.Empty || state.OperationId == Guid.Empty
            || state.IssuedAtUtc.Offset != TimeSpan.Zero || state.ExpiresAtUtc.Offset != TimeSpan.Zero
            || state.IssuedAtUtc > now || state.ExpiresAtUtc <= state.IssuedAtUtc
            || (state.ExpiryObservedAtUtc is { } observed && (observed.Offset != TimeSpan.Zero || observed < state.ExpiresAtUtc)))
            throw Unavailable();
        var current = await Receipts(scope).SingleOrDefaultAsync(x => x.BatchId == batchId && x.Epoch == state.Epoch, cancellationToken)
            ?? throw Unavailable();
        ValidateReceipt(current, scope, now);
        if (!Matches(state, current) || await Receipts(scope).LongCountAsync(x => x.BatchId == batchId, cancellationToken) != state.Epoch
            || await Receipts(scope).AnyAsync(x => x.BatchId == batchId && x.Epoch > state.Epoch, cancellationToken))
            throw Unavailable();
        return state;
    }

    private Task<GroupBatchAllocationReceipt> ReadAllocationAsync(GroupScope scope, Guid batchId, DateTimeOffset now,
        CancellationToken cancellationToken) => new GroupBatchAllocationStore(database, worker, GroupBatchTiming.InitialTuning, clock)
            .ReadAllocatedLockedAsync(scope, batchId, now, cancellationToken);
    private IQueryable<GroupBatchClaimReceiptRecord> Receipts(GroupScope scope) => database.GroupBatchClaimReceipts.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId);
    private void ValidateEntry(GroupScope scope, CancellationToken cancellationToken)
    {
        ValidateScope(scope, cancellationToken);
        if (database.ChangeTracker.HasChanges() || (database.Database.IsRelational() && !database.Database.IsSqlServer())) throw Unavailable();
    }
    private void ValidateScope(GroupScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); worker.Validate(); ArgumentNullException.ThrowIfNull(scope); scope.Validate();
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
    }
    private DateTimeOffset UtcNow()
    { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }
    private static void ValidateReceipt(GroupBatchClaimReceiptRecord record, GroupScope scope, DateTimeOffset now)
    {
        if (record.TenantId != scope.TenantId || record.CompanyId != scope.CompanyId || record.BindingId != scope.SourceBindingId
            || record.OperationId == Guid.Empty || record.BatchId == Guid.Empty || record.OwnerId == Guid.Empty || record.Epoch <= 0
            || record.RequestedLifetimeTicks < MinimumLifetime.Ticks || record.RequestedLifetimeTicks > MaximumLifetime.Ticks
            || record.IssuedAtUtc.Offset != TimeSpan.Zero || record.ExpiresAtUtc.Offset != TimeSpan.Zero || record.IssuedAtUtc > now
            || record.ExpiresAtUtc != record.IssuedAtUtc.AddTicks(record.RequestedLifetimeTicks)
            || record.ServiceId == Guid.Empty || record.CredentialEpoch <= 0 || record.GrantVersion <= 0 || record.SourceVersion <= 0
            || record.DeletionGeneration < 0 || record.AccountVersion <= 0 || record.AuthoritySha256.Length != 64
            || record.AuthoritySha256.Any(c => !"0123456789ABCDEF".Contains(c))) throw Unavailable();
    }
    private static bool Matches(GroupBatchClaimStateRecord state, GroupBatchClaimReceiptRecord receipt) =>
        state.BatchId == receipt.BatchId && state.Epoch == receipt.Epoch && state.OwnerId == receipt.OwnerId
        && state.OperationId == receipt.OperationId && state.IssuedAtUtc == receipt.IssuedAtUtc && state.ExpiresAtUtc == receipt.ExpiresAtUtc;
    private static bool IsCurrent(GroupBatchClaimStateRecord state, GroupBatchClaimReceiptRecord receipt,
        GroupExtractionAuthority authority, DateTimeOffset now) => Matches(state, receipt) && state.ExpiryObservedAtUtc is null
        && receipt.ExpiresAtUtc > now && MatchesAuthority(receipt, authority);
    private static bool MatchesAuthority(GroupBatchClaimReceiptRecord receipt, GroupExtractionAuthority authority) =>
        receipt.ServiceId == authority.Principal.ServiceId && receipt.CredentialEpoch == authority.Principal.CredentialEpoch
        && receipt.GrantVersion == authority.Grant.Version && receipt.SourceVersion == authority.Source.Version
        && receipt.DeletionGeneration == authority.Source.DeletionGeneration && receipt.AccountVersion == authority.AccountVersion
        && receipt.AuthoritySha256 == AuthorityFingerprint(authority);
    private static GroupBatchClaimReceipt Receipt(GroupScope scope, GroupBatchClaimReceiptRecord record) =>
        new(scope, record.OperationId, record.BatchId, record.OwnerId, record.Epoch, record.RequestedLifetimeTicks,
            record.IssuedAtUtc, record.ExpiresAtUtc, record.ServiceId, record.CredentialEpoch, record.GrantVersion,
            record.SourceVersion, record.DeletionGeneration, record.AccountVersion);

    internal static string AuthorityFingerprint(GroupExtractionAuthority authority)
    {
        using var stream = new MemoryStream(8192);
        try
        {
            using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
            writer.Write("aioffice-group-claim-authority-v1");
            var principal = authority.Principal; var grant = authority.Grant; var source = authority.Source;
            foreach (var id in new[] { principal.TenantId, principal.CompanyId, principal.ServiceId, grant.TenantId,
                grant.CompanyId, grant.ServiceId, grant.BindingId, source.Scope.TenantId, source.Scope.CompanyId,
                source.Scope.SourceBindingId, source.ConnectorAccountId }) writer.Write(id.ToByteArray());
            writer.Write(principal.CredentialEpoch); writer.Write(principal.IsEnabled);
            writer.Write((int)grant.Capability); writer.Write(grant.Version); writer.Write(grant.IsEnabled);
            writer.Write(source.ExternalIdentity.Provider); writer.Write(source.ExternalIdentity.AccountId);
            writer.Write(source.ExternalIdentity.GroupId); writer.Write(source.DisplayName);
            writer.Write(source.Version); writer.Write(source.DeletionGeneration); writer.Write(source.IsEnabled);
            writer.Write(authority.AccountVersion); writer.Write(authority.CredentialReference); writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        }
        finally { CryptographicOperations.ZeroMemory(stream.GetBuffer()); }
    }
    private static InvalidOperationException Unavailable() => new("Group batch claim is not available.");
}
