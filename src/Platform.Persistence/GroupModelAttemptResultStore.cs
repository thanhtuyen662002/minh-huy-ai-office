using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Private SQL recovery only. This store has no provider, worker registration,
// note commit or notification dependency. Keys use the existing scoped source
// key provider, outside SQL, between two current original-plan transactions.
internal sealed class GroupModelAttemptResultStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain,
    IGroupSourceKeyProvider keys, GroupModelAttemptResultProtector protector, GroupModelAttemptAdmissionPreparer? admissions = null)
{
    internal async Task<GroupModelAttemptResult?> ReadOriginalAsync(GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk restored, CancellationToken token = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        token = deadline.Token;
        var original = GroupModelAttemptOriginalBinding.Create(plan, restored, token);
        var metadata = await ReadUnitAsync(original, async (now, ct) =>
        {
            var row = await MetadataAsync(original, ct);
            if (row is not null) _ = await AuthenticateAsync(original, row, now, ct);
            return row;
        }, token);
        if (metadata is null) return null;
        using var key = await ResolveKeyAsync(original, metadata.EncryptionKeyId, token);
        return await ReadUnitAsync(original, async (now, ct) =>
        {
            var current = await MetadataAsync(original, ct) ?? throw Unavailable();
            if (current != metadata || key.KeyId != current.EncryptionKeyId) throw Unavailable();
            var start = await AuthenticateAsync(original, current, now, ct);
            return await DecodeAsync(original, restored, current, start, key, now, ct);
        }, token);
    }

    internal async Task<GroupModelAttemptResult> PersistOriginalAsync(GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk restored, GroupModelAttemptResponse response,
        CancellationToken token = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        token = deadline.Token;
        var original = GroupModelAttemptOriginalBinding.Create(plan, restored, token);
        var accepted = GroupModelAttemptResult.Create(response, restored, token);
        if (response.Permit.Original is not { } permitPlan
            || !ReferenceEquals(response.Permit.Context, original.Context)
            || !CryptographicOperations.FixedTimeEquals(permitPlan.Plan.Encode(), original.Plan.Encode())) throw Unavailable();
        var metadata = await UnitAsync(original, async (now, _, ct) =>
        {
            var row = await MetadataAsync(original, ct);
            if (row is not null)
            {
                var existing = await AuthenticateAsync(original, row, now, ct);
                if (existing != response.Permit.Start) throw Unavailable();
            }
            else await RequireWritableStartAsync(original, response.Permit, now, ct);
            return row;
        }, null, token, response.Permit.Admission is null ? null : response.Permit);
        using var key = await ResolveKeyAsync(original, metadata?.EncryptionKeyId, token);
        var encoded = accepted.Encode();
        try
        {
            return await UnitAsync(original, async (now, staged, ct) =>
            {
                var current = await MetadataAsync(original, ct);
                if (current is not null)
                {
                    if (metadata is null || current != metadata || current.EncryptionKeyId != key.KeyId) throw Unavailable();
                    var start = await AuthenticateAsync(original, current, now, ct);
                    if (start != response.Permit.Start) throw Unavailable();
                    var existing = await DecodeAsync(original, restored, current, start, key, now, ct);
                    var prior = existing.Encode();
                    try { if (!CryptographicOperations.FixedTimeEquals(encoded, prior)) throw Unavailable(); }
                    finally { CryptographicOperations.ZeroMemory(prior); }
                    return existing;
                }
                if (metadata is not null) throw Unavailable();
                await RequireWritableStartAsync(original, response.Permit, now, ct);
                var scope = original.Context.Scope;
                var chunk = original.Plan.Chunks[restored.Original.Ordinal - 1];
                if (accepted.Observed.ObservedAtUtc > now) throw Unavailable();
                var envelope = protector.Protect(Protection(original, response.Permit.Start.RequestId), encoded, key.Key, key.KeyId);
                var row = new GroupModelAttemptResultRecord
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    RequestId = response.Permit.Start.RequestId,
                    BatchId = original.Context.BatchId,
                    PlanSha256 = original.Plan.Sha256,
                    InputSha256 = chunk.InputSha256,
                    ChunkOrdinal = chunk.Ordinal,
                    OperationId = original.OperationId,
                    FormatVersion = 1,
                    EncryptionKeyId = key.KeyId,
                    ProtectedResultSha256 = Convert.ToHexString(SHA256.HashData(envelope)),
                    ProtectedResult = envelope,
                    StartedAtUtc = response.Permit.Start.StartedAtUtc,
                    DeadlineUtc = response.Permit.Start.DeadlineUtc,
                    ObservedAtUtc = accepted.Observed.ObservedAtUtc,
                    PersistedAtUtc = now
                };
                database.Add(row); staged.Add(row); await database.SaveChangesAsync(ct);
                if (UtcNow() >= row.DeadlineUtc) throw Unavailable();
                return accepted;
            }, metadata is null ? response.Permit.Start.DeadlineUtc : null, token, response.Permit.Admission is null ? null : response.Permit);
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    // Fixed accepted-result reads keep two complete current units separated
    // by key resolution. Shared applies only to these read units; persistence
    // and journal mutation continue using their unchanged Exclusive wrapper.
    private async Task<T> ReadUnitAsync<T>(GroupModelAttemptOriginalBinding original,
        Func<DateTimeOffset, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        var context = original.Context;
        new GroupModelAttemptJournalStore(database, worker, clock, sources, brain).RequireOriginalReadEntry(original, token);
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var expected = original.Plan.Dependencies;
        try
        {
            var began = UtcNow();
            await using var unit = await DataSourceRegistrationTransaction.BeginAsync(database, token);
            await new GroupModelAttemptPermissionVerifier(database).RequireSafeRuntimeAsync(token);
            await new GroupModelAttemptResultPermissionVerifier(database).RequireSafeRuntimeAsync(token);
            var readLock = await GroupSourceReadLock.AcquireAsync(database, context.Scope, token);
            await InspectAsync();
            var now = UtcNow(); if (now < began) throw Unavailable();
            var result = await operation(now, token);
            await InspectAsync();
            if (UtcNow() < now || database.ChangeTracker.HasChanges()) throw Unavailable();
            await unit.CommitAsync(token);
            token.ThrowIfCancellationRequested();
            if (UtcNow() < now) throw Unavailable();
            return result;

            async Task InspectAsync()
            {
                await new GroupModelAttemptPermissionVerifier(database).RequireSafeRuntimeAsync(token);
                await new GroupModelAttemptResultPermissionVerifier(database).RequireSafeRuntimeAsync(token);
                var current = await sources.RequireManifestUnchangedLockedAsync(context.Preparation.Context.Handle, expected, token, readLock);
                if (current is GroupBatchClaimFenceVerdict.Current)
                    current = await brain.RequireManifestUnchangedLockedAsync(context.Preparation.Context.Handle, expected, token, readLock);
                if (current is GroupBatchClaimFenceVerdict.Current)
                {
                    await new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain)
                        .RequireOriginalUnchangedLockedAsync(context.Preparation.Context.Handle, original.Plan, token);
                    await new GroupModelAttemptPermissionVerifier(database).RequireSafeRuntimeAsync(token);
                    await new GroupModelAttemptResultPermissionVerifier(database).RequireSafeRuntimeAsync(token);
                    current = await claims.InspectCurrentLockedAsync(context.Preparation.Context.Handle, token, readLock);
                }
                if (current is GroupBatchClaimFenceVerdict.Expired expired) throw new ReadExpired(expired.Observation);
            }
        }
        catch (ReadExpired expired)
        {
            // The await-using unit above has rolled back and restored/disposed
            // its session before this fresh Exclusive metadata-only unit.
            await claims.RetireObservedExpiryAsync(expired.Observation, token);
            throw GroupServiceDirectory.Denied();
        }
        catch (Exception error) when (error is Microsoft.Data.SqlClient.SqlException or DbUpdateException or ArgumentException or OverflowException)
        { throw Unavailable(); }
    }

    private sealed class ReadExpired(GroupBatchClaimExpiryObservation observation) : Exception
    { internal GroupBatchClaimExpiryObservation Observation { get; } = observation; }

    private async Task<T> UnitAsync<T>(GroupModelAttemptOriginalBinding original,
        Func<DateTimeOffset, List<object>, CancellationToken, Task<T>> operation,
        DateTimeOffset? writeDeadline, CancellationToken token, GroupModelAttemptPermit? admittedPermit = null)
    {
        var result = await new GroupModelAttemptJournalStore(database, worker, clock, sources, brain,
                admittedPermit is null ? null : admissions)
            .InOriginalCurrentUnitAsync(original, operation, async ct =>
            {
                await new GroupModelAttemptResultPermissionVerifier(database).RequireSafeRuntimeAsync(ct);
                if (writeDeadline is { } deadline && UtcNow() >= deadline) throw Unavailable();
            }, token, admittedPermit);
        // A commit may finish after the final in-transaction clock check. A
        // late reply cannot return a successful new write; if it committed,
        // recovery authenticates that original row with a fresh current claim.
        if (writeDeadline is { } completedDeadline && UtcNow() >= completedDeadline) throw Unavailable();
        return result;
    }

    private async Task<Metadata?> MetadataAsync(GroupModelAttemptOriginalBinding original, CancellationToken token)
    {
        var scope = original.Context.Scope;
        var rows = await database.Set<GroupModelAttemptResultRecord>().AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == original.Context.BatchId
            && x.PlanSha256 == original.Plan.Sha256 && x.OperationId == original.OperationId)
            .Select(x => new Metadata(x.RequestId, x.InputSha256, x.ChunkOrdinal, x.FormatVersion, x.EncryptionKeyId,
                x.ProtectedResultSha256, EF.Functions.DataLength(x.ProtectedResult), x.StartedAtUtc, x.DeadlineUtc,
                x.ObservedAtUtc, x.PersistedAtUtc)).Take(2).ToArrayAsync(token);
        if (rows.Length > 1) throw Unavailable();
        return rows.SingleOrDefault();
    }

    private async Task<GroupModelAttemptStarted> AuthenticateAsync(GroupModelAttemptOriginalBinding original,
        Metadata row, DateTimeOffset now, CancellationToken token)
    {
        var chunk = original.Plan.Chunks.Single(x => x.OperationId == original.OperationId);
        if (row.InputSha256 != chunk.InputSha256 || row.ChunkOrdinal != chunk.Ordinal || row.FormatVersion != 1
            || row.Length is null or < 229 or > GroupModelAttemptResultProtector.MaximumEnvelopeLength
            || !ValidKeyId(row.EncryptionKeyId) || !Hash(row.ProtectedResultSha256)
            || row.StartedAtUtc.Offset != TimeSpan.Zero || row.DeadlineUtc.Offset != TimeSpan.Zero
            || row.ObservedAtUtc.Offset != TimeSpan.Zero || row.PersistedAtUtc.Offset != TimeSpan.Zero
            || row.ObservedAtUtc < row.StartedAtUtc || row.ObservedAtUtc >= row.DeadlineUtc
            || row.PersistedAtUtc < row.ObservedAtUtc || row.PersistedAtUtc >= row.DeadlineUtc || row.PersistedAtUtc > now) throw Unavailable();
        var (starts, failures) = await JournalAsync(original, now, token);
        var start = starts.SingleOrDefault(x => x.RequestId == row.RequestId) ?? throw Unavailable();
        if (start != starts[^1] || start.StartedAtUtc != row.StartedAtUtc || start.DeadlineUtc != row.DeadlineUtc
            || failures.Any(x => x.RequestId == row.RequestId)) throw Unavailable();
        return start;
    }

    private async Task RequireWritableStartAsync(GroupModelAttemptOriginalBinding original, GroupModelAttemptPermit permit,
        DateTimeOffset now, CancellationToken token)
    {
        var (starts, failures) = await JournalAsync(original, now, token);
        var receipt = original.Context.Preparation.Context.Handle.Receipt;
        if (starts.Length == 0 || starts[^1] != permit.Start || failures.Any(x => x.RequestId == permit.Start.RequestId)
            || permit.Start.ClaimOperationId != receipt.OperationId || permit.Start.ClaimEpoch != receipt.Epoch
            || now < permit.Start.StartedAtUtc || now >= permit.Start.DeadlineUtc) throw Unavailable();
    }

    private async Task<(GroupModelAttemptStarted[] Starts, GroupModelAttemptFailed[] Failures)> JournalAsync(
        GroupModelAttemptOriginalBinding original, DateTimeOffset now, CancellationToken token)
    {
        var context = original.Context;
        var identity = GroupModelAttemptBudget.InputIdentity(context);
        var rows = await GroupModelAttemptJournalReader.ReadIncludingAcceptedAsync(database, context, identity, token);
        _ = GroupModelAttemptBudget.Read(context.Scope, context.BatchId, identity, rows.Starts, rows.Failures, now, token);
        return rows;
    }

    private async Task<GroupModelAttemptResult> DecodeAsync(GroupModelAttemptOriginalBinding original,
        GroupAutomaticInputPlan.RestoredChunk restored, Metadata metadata, GroupModelAttemptStarted start,
        GroupSourceKeyMaterial key, DateTimeOffset now, CancellationToken token)
    {
        var scope = original.Context.Scope;
        var encrypted = await BoundedResultQuery(database, scope, metadata.RequestId)
            .SingleOrDefaultAsync(token) ?? throw Unavailable();
        if (encrypted.Length != metadata.Length
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(encrypted), Convert.FromHexString(metadata.ProtectedResultSha256)))
            throw Unavailable();
        var clear = protector.Unprotect(Protection(original, start.RequestId), encrypted, key.Key, key.KeyId);
        try
        {
            var decoded = GroupModelAttemptResult.Read(clear, original.Plan, restored, start, now, token);
            if (decoded.Observed.ObservedAtUtc != metadata.ObservedAtUtc) throw Unavailable();
            return decoded;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal static IQueryable<byte[]?> BoundedResultQuery(PlatformDbContext database, GroupScope scope, Guid requestId) =>
        database.Set<GroupModelAttemptResultRecord>().AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.RequestId == requestId)
            .Select(x => EF.Functions.DataLength(x.ProtectedResult) >= 229
                && EF.Functions.DataLength(x.ProtectedResult) <= GroupModelAttemptResultProtector.MaximumEnvelopeLength
                ? x.ProtectedResult : null);

    private async Task<GroupSourceKeyMaterial> ResolveKeyAsync(GroupModelAttemptOriginalBinding original,
        string? id, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        token = deadline.Token;
        token.ThrowIfCancellationRequested();
        if (database.Database.CurrentTransaction is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        Task<GroupSourceKeyMaterial>? pending = null;
        try
        {
            pending = (id is null ? keys.ResolveWriteAsync(original.Context.Scope, token)
                : keys.ResolveReadAsync(original.Context.Scope, id, token)).AsTask();
            return await pending.WaitAsync(token) ?? throw Unavailable();
        }
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

    private static GroupModelAttemptResultProtectionContext Protection(GroupModelAttemptOriginalBinding original, Guid request)
    {
        var chunk = original.Plan.Chunks.Single(x => x.OperationId == original.OperationId);
        return new(original.Context.Scope, original.Context.BatchId, request, original.Plan.Sha256,
            chunk.Ordinal, original.OperationId, chunk.InputSha256);
    }
    private sealed record Metadata(Guid RequestId, string InputSha256, int ChunkOrdinal, int FormatVersion,
        string EncryptionKeyId, string ProtectedResultSha256, int? Length, DateTimeOffset StartedAtUtc,
        DateTimeOffset DeadlineUtc, DateTimeOffset ObservedAtUtc, DateTimeOffset PersistedAtUtc);
    private DateTimeOffset UtcNow()
    { var now = clock.GetUtcNow(); return now.Offset == TimeSpan.Zero ? now : throw Unavailable(); }
    private static bool ValidKeyId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static InvalidOperationException Unavailable() => new("Group model attempt stored result is unavailable.");
}
