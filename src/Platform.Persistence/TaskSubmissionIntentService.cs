using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class TaskSubmissionIntentConflictException(string code) : InvalidOperationException("Task submission intent conflicts.")
{
    public string Code { get; } = code;
}

public sealed class TaskSubmissionIntentNotFoundException : Exception;

/// <summary>Append-only preparation and read-only recovery. Only explicit Execute creates a task.</summary>
public sealed class TaskSubmissionIntentService
{
    private readonly PlatformDbContext database;
    private readonly TimeProvider clock;
    private readonly TaskSubmissionAdmission admission;
    private readonly TaskSubmissionIntentPermissionVerifier permissions;
    private readonly PilotTaskSubmissionService submission;

    public TaskSubmissionIntentService(PlatformDbContext database, IAuthorizationDirectory directory,
        TimeProvider? timeProvider = null, DataSourceSecretBindingService? bindingService = null)
    {
        this.database = database;
        clock = timeProvider ?? TimeProvider.System;
        var bindings = bindingService ?? new(database, directory);
        admission = new(database, directory, bindings);
        permissions = new(database);
        submission = new(database, directory, clock, bindings);
    }

    public async Task<TaskSubmissionIntentDetail> PrepareAsync(AuthorizationContext authority,
        TaskSubmissionPrepareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var fingerprint = TaskSubmissionIntentIdentity.Fingerprint(request);
        RequireCleanTracker();
        await admission.RequireAuthorityAsync(authority, cancellationToken);
        TaskSubmissionIntentDetail detail;
        TaskSubmissionIntentRecord? added = null;
        await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken))
        {
            try
            {
                await admission.LockCompanyAsync(authority, cancellationToken);
                await admission.RequireExecutionAsync(authority, request.DataSourceId, cancellationToken);
                await permissions.RequireAppendOnlyAsync(cancellationToken);
                var previous = await FindAsync(authority, request.OperationId, cancellationToken);
                if (previous is not null)
                {
                    var stored = Input(previous) ?? throw Conflict("intent-unavailable");
                    if (stored != request || previous.InputFingerprint != fingerprint) throw Conflict("operation-conflict");
                    detail = await ProjectAsync(authority, previous, cancellationToken);
                }
                else
                {
                    // A legacy caller can already have committed this reserved key.
                    // Compare its original request before preparing any new row.
                    var pilot = Pilot(request);
                    var existing = await FindTaskAsync(authority, pilot.IdempotencyKey, cancellationToken);
                    if (existing is not null) await RequireCompatibleTaskAsync(authority, existing, pilot, cancellationToken);
                    else await RequireQuotaAsync(authority, cancellationToken);
                    var now = clock.GetUtcNow();
                    added = new()
                    {
                        TenantId = authority.TenantId,
                        CompanyId = authority.CompanyId,
                        UserId = authority.UserId,
                        OperationId = request.OperationId,
                        DataSourceId = request.DataSourceId,
                        InputVersion = TaskSubmissionIntentIdentity.InputVersion,
                        MaxAttempts = TaskSubmissionIntentIdentity.MaximumAttempts,
                        Question = request.Question,
                        InputFingerprint = fingerprint,
                        CreatedAtUtc = now,
                        ExpiresAtUtc = now + TaskSubmissionIntentIdentity.PreparationLifetime
                    };
                    await permissions.RequireAppendOnlyAsync(cancellationToken);
                    await admission.RequireExecutionAsync(authority, request.DataSourceId, cancellationToken);
                    database.TaskSubmissionIntents.Add(added);
                    await database.SaveChangesAsync(cancellationToken);
                    var persisted = await FindAsync(authority, request.OperationId, cancellationToken)
                        ?? throw Conflict("intent-unavailable");
                    detail = await ProjectAsync(authority, persisted, cancellationToken);
                }
                await admission.RequireExecutionAsync(authority, request.DataSourceId, cancellationToken);
                await permissions.RequireAppendOnlyAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                if (added is not null) database.Entry(added).State = EntityState.Detached;
                throw;
            }
        }
        await admission.RequireExecutionAsync(authority, request.DataSourceId, cancellationToken);
        await permissions.RequireAppendOnlyAsync(cancellationToken);
        return detail;
    }

    // Legacy Core callers retain their header contract, but cannot replace the
    // immutable prepared body by using the browser operation's reserved key.
    internal async Task RequireLegacyCompatibilityAsync(AuthorizationContext authority, PilotTaskSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        const string prefix = "web-intent-v1-";
        if (!request.IdempotencyKey.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(request.IdempotencyKey[prefix.Length..], "N", out var operation)
            || operation == Guid.Empty || request.IdempotencyKey != TaskSubmissionIntentIdentity.IdempotencyKey(operation)) return;
        await RequireReadAsync(authority, cancellationToken);
        var row = await FindAsync(authority, operation, cancellationToken);
        if (row is null) return;
        var input = Input(row) ?? throw Conflict("intent-unavailable");
        if (Pilot(input) != request) throw Conflict("operation-conflict");
        var detail = await ProjectAsync(authority, row, cancellationToken);
        if (detail.State == TaskSubmissionIntentState.Unavailable) throw Conflict("intent-unavailable");
        if (detail.State == TaskSubmissionIntentState.Expired) throw Conflict("intent-expired");
    }

    public async Task<TaskSubmissionAcceptedReceipt> ExecuteAsync(AuthorizationContext authority, Guid operationId,
        TaskSubmissionExecuteRequest request, CancellationToken cancellationToken = default)
    {
        TaskSubmissionIntentIdentity.RequireOperation(operationId);
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        RequireCleanTracker();
        await admission.RequireAuthorityAsync(authority, cancellationToken);
        TaskSubmissionAcceptedReceipt receipt;
        Guid sourceId;
        await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken))
        {
            await admission.LockCompanyAsync(authority, cancellationToken);
            await admission.RequireAuthorityAsync(authority, cancellationToken);
            await permissions.RequireAppendOnlyAsync(cancellationToken);
            var stored = await FindAsync(authority, operationId, cancellationToken) ?? throw new TaskSubmissionIntentNotFoundException();
            var input = Input(stored) ?? throw Conflict("intent-unavailable");
            if (stored.InputFingerprint != request.InputFingerprint) throw Conflict("operation-conflict");
            sourceId = input.DataSourceId;
            await admission.RequireExecutionAsync(authority, sourceId, cancellationToken);
            var detail = await ProjectAsync(authority, stored, cancellationToken);
            if (detail.State == TaskSubmissionIntentState.Expired) throw Conflict("intent-expired");
            if (detail.State == TaskSubmissionIntentState.Unavailable) throw Conflict("intent-unavailable");
            var accepted = await submission.SubmitWithinTransactionAsync(authority, Pilot(input), cancellationToken);
            receipt = Receipt(authority, stored, accepted);
            await permissions.RequireAppendOnlyAsync(cancellationToken);
            await admission.RequireExecutionAsync(authority, sourceId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // This final fence can deny after commit; GET recovery retains that fact.
        await permissions.RequireAppendOnlyAsync(cancellationToken);
        await admission.RequireExecutionAsync(authority, sourceId, cancellationToken);
        return receipt;
    }

    public async Task<TaskSubmissionIntentDetail?> GetAsync(AuthorizationContext authority, Guid operationId,
        CancellationToken cancellationToken = default)
    {
        TaskSubmissionIntentIdentity.RequireOperation(operationId);
        await RequireReadAsync(authority, cancellationToken);
        var row = await FindAsync(authority, operationId, cancellationToken);
        var detail = row is null ? null : await ProjectAsync(authority, row, cancellationToken);
        await RequireReadAsync(authority, cancellationToken);
        return detail;
    }

    public async Task<TaskSubmissionIntentPage> ListAsync(AuthorizationContext authority, int offset = 0, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (offset is < 0 or > TaskSubmissionIntentIdentity.MaximumOffset || limit is < 1 or > TaskSubmissionIntentIdentity.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(offset));
        await RequireReadAsync(authority, cancellationToken);
        var rows = await Owned(authority).OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.OperationId)
            .Skip(offset).Take(limit + 1).ToArrayAsync(cancellationToken);
        var details = new List<TaskSubmissionIntentDetail>();
        foreach (var row in rows.Take(limit)) details.Add(await ProjectAsync(authority, row, cancellationToken));
        await RequireReadAsync(authority, cancellationToken);
        return new(authority.CompanyId, details, offset, limit, rows.Length > limit);
    }

    private async Task RequireQuotaAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        var unresolved = 0;
        var now = clock.GetUtcNow();
        // Bounded batches avoid loading unbounded private questions. Invalid
        // existing task evidence consumes quota; mere Task existence excludes nothing.
        const int batchSize = 25;
        for (var offset = 0; offset <= TaskSubmissionIntentIdentity.MaximumOffset; offset += batchSize)
        {
            var rows = await Owned(authority).Where(row => row.ExpiresAtUtc > now)
                .OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.OperationId)
                .Skip(offset).Take(batchSize).ToArrayAsync(cancellationToken);
            foreach (var row in rows)
            {
                var detail = await ProjectAsync(authority, row, cancellationToken);
                if (detail.State != TaskSubmissionIntentState.Accepted && ++unresolved >= TaskSubmissionIntentIdentity.MaximumActiveIntents)
                    throw Conflict("intent-limit");
            }
            if (rows.Length < batchSize) return;
        }
        throw Conflict("intent-limit");
    }

    private async Task<TaskSubmissionIntentDetail> ProjectAsync(AuthorizationContext authority, StoredIntent row,
        CancellationToken cancellationToken)
    {
        var input = Input(row);
        if (input is null) return Unavailable(authority, row.OperationId);
        var state = row.ExpiresAtUtc <= clock.GetUtcNow() ? TaskSubmissionIntentState.Expired : TaskSubmissionIntentState.Prepared;
        TaskSubmissionAcceptedReceipt? receipt = null;
        var pilot = Pilot(input);
        TaskRecord? task;
        try { task = await FindTaskAsync(authority, pilot.IdempotencyKey, cancellationToken); }
        catch (InvalidOperationException) { return Unavailable(authority, row.OperationId); }
        if (task is not null)
        {
            if (task.CreatedByUserId != authority.UserId) return Unavailable(authority, row.OperationId);
            try
            {
                var accepted = await RequireCompatibleTaskAsync(authority, task, pilot, cancellationToken);
                receipt = Receipt(authority, row, accepted);
                state = TaskSubmissionIntentState.Accepted;
            }
            catch (InvalidOperationException) { return Unavailable(authority, row.OperationId); }
        }
        return new(authority.CompanyId, row.OperationId, state, input.DataSourceId, input.Question, row.InputFingerprint,
            row.CreatedAtUtc, row.ExpiresAtUtc, receipt);
    }

    private async Task<PilotTaskSubmissionAccepted> RequireCompatibleTaskAsync(AuthorizationContext authority, TaskRecord task,
        PilotTaskSubmissionRequest request, CancellationToken cancellationToken)
    {
        var original = await PilotTaskStoredRequest.ReadAsync(database, authority, task.Id, cancellationToken);
        if (task.CreatedByUserId != authority.UserId || original is null) throw Conflict("intent-unavailable");
        if (!PilotTaskStoredRequest.Matches(original, request)) throw Conflict("operation-conflict");
        return await submission.ReadExistingAsync(authority, task, request, cancellationToken);
    }

    private Task<TaskRecord?> FindTaskAsync(AuthorizationContext authority, string key, CancellationToken cancellationToken)
    {
        var taskId = PilotTaskIdentity.ForTask(authority, key);
        return database.Tasks.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == authority.TenantId
            && row.CompanyId == authority.CompanyId && row.Id == taskId, cancellationToken);
    }

    private async Task RequireReadAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        await admission.RequireAuthorityAsync(authority, cancellationToken);
        await permissions.RequireAppendOnlyAsync(cancellationToken);
    }

    private void RequireCleanTracker()
    {
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Task submission is unavailable.");
    }

    private Task<StoredIntent?> FindAsync(AuthorizationContext authority, Guid operationId, CancellationToken cancellationToken) =>
        Owned(authority).SingleOrDefaultAsync(row => row.OperationId == operationId, cancellationToken);

    private IQueryable<StoredIntent> Owned(AuthorizationContext authority)
    {
        var query = database.Database.IsSqlServer()
            ? database.Database.SqlQuery<StoredIntent>($"""
                SELECT TenantId,CompanyId,UserId,OperationId,DataSourceId,InputVersion,MaxAttempts,InputFingerprint,CreatedAtUtc,ExpiresAtUtc,
                  CASE WHEN DATALENGTH(Question)<=8000 THEN CONVERT(varbinary(max),Question) ELSE NULL END AS QuestionBytes,
                  CAST(NULL AS nvarchar(max)) AS QuestionText FROM aioffice.TaskSubmissionIntents
                """)
            : database.TaskSubmissionIntents.AsNoTracking().Select(row => new StoredIntent(row.TenantId, row.CompanyId, row.UserId,
                row.OperationId, row.DataSourceId, row.InputVersion, row.MaxAttempts, row.InputFingerprint,
                row.CreatedAtUtc, row.ExpiresAtUtc, null, row.Question));
        return query.Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.UserId == authority.UserId);
    }

    private static TaskSubmissionPrepareRequest? Input(StoredIntent row)
    {
        var question = TaskHistoryMetadata.Decode(row.QuestionBytes, row.QuestionText, CustomerPilotTaskRequest.MaximumQuestionLength * 2);
        if (question is null || row.InputVersion != TaskSubmissionIntentIdentity.InputVersion
            || row.MaxAttempts != TaskSubmissionIntentIdentity.MaximumAttempts || row.CreatedAtUtc == default
            || !TaskSubmissionIntentIdentity.IsFingerprint(row.InputFingerprint)) return null;
        try
        {
            if (row.ExpiresAtUtc != row.CreatedAtUtc + TaskSubmissionIntentIdentity.PreparationLifetime) return null;
            var request = new TaskSubmissionPrepareRequest(row.OperationId, row.DataSourceId, question);
            return TaskSubmissionIntentIdentity.Fingerprint(request) == row.InputFingerprint ? request : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static PilotTaskSubmissionRequest Pilot(TaskSubmissionPrepareRequest input) => new(
        TaskSubmissionIntentIdentity.IdempotencyKey(input.OperationId), input.DataSourceId, input.Question, TaskSubmissionIntentIdentity.MaximumAttempts);
    private static TaskSubmissionAcceptedReceipt Receipt(AuthorizationContext authority, StoredIntent row, PilotTaskSubmissionAccepted accepted) =>
        new(authority.CompanyId, row.OperationId, row.DataSourceId, row.InputFingerprint, accepted.TaskId, accepted.StepId,
            PilotTaskIdentity.ForMessage(accepted.TaskId, accepted.StepId), accepted.TaskStatus, accepted.DispatchState, accepted.CreatedAtUtc);
    private static TaskSubmissionIntentDetail Unavailable(AuthorizationContext authority, Guid operationId) =>
        new(authority.CompanyId, operationId, TaskSubmissionIntentState.Unavailable, null, null, null, null, null, null);
    private static TaskSubmissionIntentConflictException Conflict(string code) => new(code);
    private sealed record StoredIntent(Guid TenantId, Guid CompanyId, Guid UserId, Guid OperationId, Guid DataSourceId,
        int InputVersion, int MaxAttempts, string InputFingerprint, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
        byte[]? QuestionBytes, string? QuestionText);
}
