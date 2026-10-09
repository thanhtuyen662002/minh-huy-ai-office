using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Creates the first executable pilot task without accepting caller-supplied tenant, company or
/// user authority. The deterministic task identity is scoped to the server-derived authority and
/// idempotency key, while the request event remains the durable source used to detect conflicting
/// replays. A pending dispatch is an outbox item; a worker may publish it only after this
/// transaction commits.
/// </summary>
public sealed class PilotTaskSubmissionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PlatformDbContext dbContext;
    private readonly IAuthorizationDirectory authorizationDirectory;
    private readonly TimeProvider timeProvider;
    private readonly DataSourceSecretBindingService bindings;

    public PilotTaskSubmissionService(
        PlatformDbContext dbContext,
        IAuthorizationDirectory authorizationDirectory,
        TimeProvider? timeProvider = null,
        DataSourceSecretBindingService? bindingService = null)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.authorizationDirectory = authorizationDirectory ?? throw new ArgumentNullException(nameof(authorizationDirectory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        bindings = bindingService ?? new(dbContext, authorizationDirectory);
    }

    /// <summary>
    /// Accepts the customer-facing request and an idempotency key obtained from the transport
    /// header. The returned message identity is the same identity persisted in the dispatch
    /// outbox, so a publisher can safely retry after a process interruption.
    /// </summary>
    public async Task<CustomerPilotTaskSubmissionResult> SubmitAsync(
        AuthorizationContext authority,
        CustomerPilotTaskRequest request,
        string idempotencyKey,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var accepted = await SubmitAsync(
                authority,
                new PilotTaskSubmissionRequest(
                    idempotencyKey,
                    request.DataSourceId,
                    request.Question,
                    maxAttempts),
                cancellationToken)
            .ConfigureAwait(false);

        return new CustomerPilotTaskSubmissionResult(
            accepted.TaskId,
            accepted.StepId,
            PilotTaskIdentity.ForMessage(accepted.TaskId, accepted.StepId),
            accepted.TaskStatus)
        {
            IdempotencyKey = accepted.IdempotencyKey,
            DispatchState = accepted.DispatchState,
            CreatedAtUtc = accepted.CreatedAtUtc
        };
    }

    public async Task<PilotTaskSubmissionAccepted> SubmitAsync(
        AuthorizationContext authority,
        PilotTaskSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var authorized = await authorizationDirectory.ResolveAsync(authority, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("An active server-derived company membership is required.");


        var trustedAuthority = authorized.Context;
        var taskId = PilotTaskIdentity.ForTask(trustedAuthority, request.IdempotencyKey);

        // The deterministic identity makes an HTTP retry a normal read even when the original
        // request has already reached the database. The durable event below is still compared so
        // reusing a key with different business input fails closed.
        var existing = await FindTaskAsync(trustedAuthority, taskId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await ValidateDataSourceAsync(trustedAuthority, request.DataSourceId, cancellationToken);
            return await ReadExistingAsync(trustedAuthority, existing, request, cancellationToken)
                .ConfigureAwait(false);
        }

        await ValidateDataSourceAsync(trustedAuthority, request.DataSourceId, cancellationToken)
            .ConfigureAwait(false);

        var nowUtc = timeProvider.GetUtcNow();
        var stepId = PilotTaskIdentity.ForStep(taskId);
        var messageId = PilotTaskIdentity.ForMessage(taskId, stepId);
        var requestEvent = new PilotTaskRequestEvent(
            request.IdempotencyKey,
            request.DataSourceId,
            request.Question,
            request.MaxAttempts);
        requestEvent.Validate();

        var task = new TaskRecord
        {
            TenantId = trustedAuthority.TenantId,
            CompanyId = trustedAuthority.CompanyId,
            Id = taskId,
            CreatedByUserId = trustedAuthority.UserId,
            Status = TaskExecutionStatus.Pending,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        var step = new TaskStepRecord
        {
            TenantId = trustedAuthority.TenantId,
            CompanyId = trustedAuthority.CompanyId,
            TaskId = taskId,
            Id = stepId,
            StepKey = PilotTaskIdentity.StepKey,
            Status = TaskStepStatus.Ready,
            Attempt = 0,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        var execution = WorkerExecutionStateMachine.Initialize(
            trustedAuthority.TenantId,
            trustedAuthority.CompanyId,
            taskId,
            stepId,
            nowUtc);
        var dispatch = WorkerExecutionStateMachine.CreateDispatch(
            execution,
            messageId,
            checkpointVersion: null,
            availableAtUtc: nowUtc,
            nowUtc);
        step.Attempt = execution.Attempt;

        var durableEvent = new TaskEventRecord
        {
            TenantId = trustedAuthority.TenantId,
            CompanyId = trustedAuthority.CompanyId,
            TaskId = taskId,
            Sequence = 1,
            StepId = stepId,
            EventType = PilotTaskRequestEvent.EventType,
            PayloadJson = JsonSerializer.Serialize(requestEvent, JsonOptions),
            OccurredAtUtc = nowUtc
        };

        // Keep the entire task graph and outbox item in one database transaction. A worker can
        // safely publish only after the transaction commits; a failed request leaves no partial
        // task, step or dispatch for a later consumer to interpret.
        await using var transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the transaction. A concurrent request with the same
            // deterministic identity may have committed between the first read and this point.
            existing = await FindTaskAsync(trustedAuthority, taskId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await ValidateDataSourceAsync(trustedAuthority, request.DataSourceId, cancellationToken);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return await ReadExistingAsync(trustedAuthority, existing, request, cancellationToken)
                    .ConfigureAwait(false);
            }

            dbContext.Tasks.Add(task);
            dbContext.TaskSteps.Add(step);
            dbContext.TaskStepExecutions.Add(execution);
            dbContext.TaskDispatches.Add(dispatch);
            dbContext.TaskEvents.Add(durableEvent);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            DetachUncommittedGraph(task, step, execution, dispatch, durableEvent);

            // Two requests with the same authority and idempotency key can pass the initial
            // read concurrently. The database key is the final fence; once the losing
            // transaction rolls back, resolve the winner and apply the same durable replay
            // comparison instead of surfacing a transient duplicate-key 500.
            var concurrent = await FindTaskAsync(trustedAuthority, taskId, cancellationToken)
                .ConfigureAwait(false);
            if (concurrent is not null)
            {
                await ValidateDataSourceAsync(trustedAuthority, request.DataSourceId, cancellationToken);
                return await ReadExistingAsync(
                        trustedAuthority,
                        concurrent,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                "The pilot task could not be persisted.",
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new PilotTaskSubmissionAccepted(
            task.Id,
            step.Id,
            request.IdempotencyKey,
            task.Status,
            dispatch.State,
            task.CreatedAtUtc);
    }

    private void DetachUncommittedGraph(
        TaskRecord task,
        TaskStepRecord step,
        TaskStepExecutionRecord execution,
        TaskDispatchRecord dispatch,
        TaskEventRecord durableEvent)
    {
        // A failed EF SaveChanges leaves Added entries tracked. Detaching only the graph created
        // by this request allows the post-rollback replay query to see the committed winner and
        // prevents a later request on the same scoped DbContext from trying to insert the failed
        // graph again.
        dbContext.Entry(task).State = EntityState.Detached;
        dbContext.Entry(step).State = EntityState.Detached;
        dbContext.Entry(execution).State = EntityState.Detached;
        dbContext.Entry(dispatch).State = EntityState.Detached;
        dbContext.Entry(durableEvent).State = EntityState.Detached;
    }

    private async Task<TaskRecord?> FindTaskAsync(
        AuthorizationContext authority,
        Guid taskId,
        CancellationToken cancellationToken) =>
        await dbContext.Tasks
            .SingleOrDefaultAsync(
                task => task.TenantId == authority.TenantId
                    && task.CompanyId == authority.CompanyId
                    && task.Id == taskId,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<PilotTaskSubmissionAccepted> ReadExistingAsync(
        AuthorizationContext authority,
        TaskRecord task,
        PilotTaskSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (task.CreatedByUserId != authority.UserId)
        {
            throw new UnauthorizedAccessException("The durable task owner does not match the authenticated authority.");
        }

        var expectedStepId = PilotTaskIdentity.ForStep(task.Id);
        var durableEvent = await dbContext.TaskEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == authority.TenantId
                    && item.CompanyId == authority.CompanyId
                    && item.TaskId == task.Id
                    && item.StepId == expectedStepId
                    && item.Sequence == 1
                    && item.EventType == PilotTaskRequestEvent.EventType,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The durable pilot task request event is missing.");

        PilotTaskRequestEvent persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<PilotTaskRequestEvent>(durableEvent.PayloadJson, JsonOptions)
                ?? throw new JsonException("The durable pilot task request event is empty.");
            persisted.Validate();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The durable pilot task request event is malformed.", exception);
        }

        if (!string.Equals(persisted.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
            || persisted.DataSourceId != request.DataSourceId
            || !string.Equals(persisted.Question, request.Question, StringComparison.Ordinal)
            || persisted.MaxAttempts != request.MaxAttempts)
        {
            throw new InvalidOperationException("The idempotency key was reused with conflicting pilot task input.");
        }

        var dispatch = await dbContext.TaskDispatches
            .AsNoTracking()
            .Where(item => item.TenantId == authority.TenantId
                && item.CompanyId == authority.CompanyId
                && item.TaskId == task.Id
                && item.StepId == expectedStepId)
            .OrderByDescending(item => item.Attempt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The durable pilot task dispatch is missing.");

        return new PilotTaskSubmissionAccepted(
            task.Id,
            expectedStepId,
            persisted.IdempotencyKey,
            task.Status,
            dispatch.State,
            task.CreatedAtUtc);
    }

    private async Task ValidateDataSourceAsync(
        AuthorizationContext authority,
        Guid dataSourceId,
        CancellationToken cancellationToken)
    {
        await bindings.RequireSourceAsync(authority, dataSourceId, readOnly: true, cancellationToken);
        var dataSource = await dbContext.DataSources
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == authority.TenantId
                    && item.CompanyId == authority.CompanyId
                    && item.Id == dataSourceId,
                cancellationToken)
            .ConfigureAwait(false);

        // Deliberately use one generic failure for missing, cross-company and disabled sources so
        // a caller cannot turn this endpoint into a data-source existence oracle.
        if (dataSource is null
            || !dataSource.IsEnabled
            || !dataSource.AllowRead
            || dataSource.MaxConcurrency is < 1 or > 1024
            || !SecretReference.TryParse(dataSource.ConnectionSecretReference, out _))
        {
            throw new UnauthorizedAccessException("The requested data source is not available for read-only pilot execution.");
        }
    }

    private async ValueTask<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational())
        {
            return await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        return new NoopDbContextTransaction();
    }

    /// <summary>
    /// EF Core's in-memory provider has no transaction implementation. Tests still exercise the
    /// complete graph atomically from the service's perspective without weakening the SQL Server
    /// production path, which always uses a real transaction.
    /// </summary>
    private sealed class NoopDbContextTransaction : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Commit() { }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Rollback() { }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void CreateSavepoint(string name) { }

        public void RollbackToSavepoint(string name) { }

        public void ReleaseSavepoint(string name) { }
    }
}

/// <summary>
/// Reads the durable status/result projection for the authenticated company scope. The task id is
/// treated as an opaque lookup key; tenant/company/user authority always comes from the directory.
/// </summary>
public sealed class PilotTaskResultProjection
{
    private readonly PlatformDbContext dbContext;
    private readonly IAuthorizationDirectory authorizationDirectory;

    public PilotTaskResultProjection(
        PlatformDbContext dbContext,
        IAuthorizationDirectory authorizationDirectory)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.authorizationDirectory = authorizationDirectory ?? throw new ArgumentNullException(nameof(authorizationDirectory));
    }

    public async Task<PilotTaskStatusSnapshot?> GetAsync(
        AuthorizationContext authority,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("Task identity is required.", nameof(taskId));
        }

        var authorized = await authorizationDirectory.ResolveAsync(authority, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("An active server-derived company membership is required.");
        if (authorized.Context != authority) throw new UnauthorizedAccessException("Company authority changed.");

        var task = await dbContext.Tasks
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == authorized.Context.TenantId
                    && item.CompanyId == authorized.Context.CompanyId
                    && item.Id == taskId
                    && item.CreatedByUserId == authorized.Context.UserId,
                cancellationToken)
            .ConfigureAwait(false);
        if (task is null)
        {
            await RequireCurrentAuthorityAsync(authority, cancellationToken);
            return null;
        }

        var step = await dbContext.TaskSteps
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == task.TenantId
                    && item.CompanyId == task.CompanyId
                    && item.TaskId == task.Id
                    && item.Id == PilotTaskIdentity.ForStep(task.Id),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The durable pilot task step is missing.");

        var execution = await dbContext.TaskStepExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == task.TenantId
                    && item.CompanyId == task.CompanyId
                    && item.TaskId == task.Id
                    && item.StepId == step.Id,
                cancellationToken)
            ?? throw new InvalidOperationException("The durable pilot task execution state is missing.");

        var dispatch = await dbContext.TaskDispatches
            .AsNoTracking()
            .Where(item => item.TenantId == task.TenantId
                && item.CompanyId == task.CompanyId
                && item.TaskId == task.Id
                && item.StepId == step.Id)
            .OrderByDescending(item => item.Attempt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The durable pilot task dispatch is missing.");

        var checkpoint = await dbContext.TaskCheckpoints
            .AsNoTracking()
            .Where(item => item.TenantId == task.TenantId
                && item.CompanyId == task.CompanyId
                && item.TaskId == task.Id
                && item.StepId == step.Id)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var updatedAtUtc = task.UpdatedAtUtc;
        if (step.UpdatedAtUtc > updatedAtUtc) updatedAtUtc = step.UpdatedAtUtc;
        if (execution.UpdatedAtUtc > updatedAtUtc) updatedAtUtc = execution.UpdatedAtUtc;

        await RequireCurrentAuthorityAsync(authority, cancellationToken);
        return new PilotTaskStatusSnapshot(
            task.Id,
            step.Id,
            task.Status,
            step.Status,
            dispatch.State,
            execution.Attempt,
            updatedAtUtc,
            checkpoint?.PayloadJson,
            execution.LastFailureClass?.ToString());
    }

    private async Task RequireCurrentAuthorityAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        var current = await authorizationDirectory.ResolveAsync(authority, cancellationToken);
        if (current?.Context != authority) throw new UnauthorizedAccessException("An active company membership is required.");
    }
}

/// <summary>
/// Public result-service name used by API composition. The projection implementation remains
/// separate so existing callers can continue using the descriptive projection name while the
/// pilot route depends on a service boundary.
/// </summary>
public sealed class PilotTaskResultService
{
    private readonly PilotTaskResultProjection projection;

    public PilotTaskResultService(
        PlatformDbContext dbContext,
        IAuthorizationDirectory authorizationDirectory)
    {
        projection = new PilotTaskResultProjection(dbContext, authorizationDirectory);
    }

    public Task<PilotTaskStatusSnapshot?> GetAsync(
        AuthorizationContext authority,
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        projection.GetAsync(authority, taskId, cancellationToken);
}
