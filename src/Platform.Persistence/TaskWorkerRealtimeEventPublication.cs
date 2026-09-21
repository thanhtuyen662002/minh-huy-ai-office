using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public interface ITaskWorkerRealtimeEventPublisher
{
    Task PublishAsync(TaskWorkerRealtimeEvent realtimeEvent, CancellationToken cancellationToken = default);
}

public sealed class TaskWorkerRealtimeEventPublication(
    TaskWorkerRealtimeEventProjector projector,
    ITaskWorkerRealtimeEventPublisher publisher)
{
    public async Task<TaskWorkerRealtimeEvent> PersistThenPublishAsync(
        TaskEventRecord durableEvent,
        Func<TaskEventRecord, CancellationToken, Task> persistAuthoritativeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(durableEvent);
        ArgumentNullException.ThrowIfNull(persistAuthoritativeEvent);

        // Validate/project before persistence so malformed events fail closed without creating authority.
        var realtimeEvent = projector.Project(durableEvent);

        // The durable transition is authoritative. Publication is attempted only after persistence completes.
        await persistAuthoritativeEvent(durableEvent, cancellationToken).ConfigureAwait(false);
        await publisher.PublishAsync(realtimeEvent, cancellationToken).ConfigureAwait(false);
        return realtimeEvent;
    }
}
