using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api;

/// <summary>
/// Publishes committed pilot dispatches after the task graph transaction has completed. A
/// Published row is deliberately retried until the worker durably acknowledges it, so a process
/// interruption after RabbitMQ confirms a message but before the database update only produces a
/// same-MessageId redelivery; the worker's durable dispatch identity makes that replay harmless.
/// </summary>
public sealed class PilotTaskDispatchOutboxHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PilotTaskDispatchOutboxHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PublishRetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(stoppingToken);
                // Published rows remain eligible until the worker acknowledgement is durable.
                // Keep retries bounded so a broker confirm/ack interruption cannot create a hot
                // loop that floods RabbitMQ with the same MessageId.
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Pilot task dispatch outbox pass failed; retrying.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IWorkEnvelopePublisher>();
        var nowUtc = DateTimeOffset.UtcNow;
        var retryPublishedBefore = nowUtc - PublishRetryDelay;

        var dispatches = await dbContext.TaskDispatches
            .Where(item =>
                (item.State == WorkDispatchState.Pending
                    || (item.State == WorkDispatchState.Published
                        && (item.PublishedAtUtc == null || item.PublishedAtUtc <= retryPublishedBefore)))
                && item.AcknowledgedAtUtc == null
                && item.DeadLetteredAtUtc == null
                && item.AvailableAtUtc <= nowUtc)
            .OrderBy(item => item.CreatedAtUtc)
            .Take(32)
            .ToListAsync(cancellationToken);

        if (dispatches.Count == 0)
        {
            return;
        }

        foreach (var dispatch in dispatches)
        {
            if (dispatch.State == WorkDispatchState.Pending)
            {
                WorkerExecutionStateMachine.TransitionDispatch(
                    dispatch,
                    WorkDispatchState.Published,
                    nowUtc);
                var task = await dbContext.Tasks.SingleOrDefaultAsync(item =>
                    item.TenantId == dispatch.TenantId
                    && item.CompanyId == dispatch.CompanyId
                    && item.Id == dispatch.TaskId,
                    cancellationToken);
                var step = await dbContext.TaskSteps.SingleOrDefaultAsync(item =>
                    item.TenantId == dispatch.TenantId
                    && item.CompanyId == dispatch.CompanyId
                    && item.TaskId == dispatch.TaskId
                    && item.Id == dispatch.StepId,
                    cancellationToken);
                if (task is not null && task.Status == TaskExecutionStatus.Pending)
                {
                    task.Status = TaskExecutionStatus.Running;
                    task.UpdatedAtUtc = nowUtc;
                }
                if (step is not null
                    && (step.Status is TaskStepStatus.Pending or TaskStepStatus.Ready))
                {
                    step.Status = TaskStepStatus.Running;
                    step.UpdatedAtUtc = nowUtc;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var envelope = WorkDispatchEnvelope.Create(
                dispatch.MessageId,
                dispatch.TenantId,
                dispatch.CompanyId,
                dispatch.TaskId,
                dispatch.StepId,
                dispatch.Attempt,
                dispatch.CheckpointVersion,
                dispatch.PublishedAtUtc ?? dispatch.CreatedAtUtc);

            try
            {
                await publisher.PublishAsync(envelope, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Pilot task dispatch {MessageId} could not be published; it remains durable for retry.",
                    dispatch.MessageId);
            }
        }
    }
}
