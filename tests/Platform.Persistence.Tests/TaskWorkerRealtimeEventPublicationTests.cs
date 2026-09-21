using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaskWorkerRealtimeEventPublicationTests
{
    [Fact]
    public async Task PersistThenPublish_PersistsBeforePublishing()
    {
        var calls = new List<string>();
        var publisher = new RecordingPublisher(calls);
        var service = new TaskWorkerRealtimeEventPublication(new TaskWorkerRealtimeEventProjector(), publisher);
        var source = CreateEvent();

        await service.PersistThenPublishAsync(source, (_, _) =>
        {
            calls.Add("persist");
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "persist", "publish" }, calls);
    }

    [Fact]
    public async Task PersistThenPublish_PersistenceFailureNeverPublishes()
    {
        var calls = new List<string>();
        var publisher = new RecordingPublisher(calls);
        var service = new TaskWorkerRealtimeEventPublication(new TaskWorkerRealtimeEventProjector(), publisher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PersistThenPublishAsync(CreateEvent(), (_, _) =>
            throw new InvalidOperationException("commit failed")));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task PersistThenPublish_PublishFailureLeavesDeterministicRedeliveryIdentity()
    {
        var publisher = new FailingPublisher();
        var service = new TaskWorkerRealtimeEventPublication(new TaskWorkerRealtimeEventProjector(), publisher);
        var source = CreateEvent();
        var persisted = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PersistThenPublishAsync(source, (_, _) =>
        {
            persisted++;
            return Task.CompletedTask;
        }));

        var recovered = new TaskWorkerRealtimeEventProjector().Project(source);
        Assert.Equal(1, persisted);
        Assert.Equal(publisher.AttemptedEventId, recovered.EventId);
        Assert.Equal(source.Sequence, recovered.Sequence);
    }

    private static TaskEventRecord CreateEvent() => new()
    {
        TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        TaskId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Sequence = 23,
        EventType = "task.status.changed",
        PayloadJson = "{\"status\":\"Running\"}",
        OccurredAtUtc = DateTimeOffset.Parse("2026-09-21T10:00:00Z")
    };

    private sealed class RecordingPublisher(List<string> calls) : ITaskWorkerRealtimeEventPublisher
    {
        public Task PublishAsync(TaskWorkerRealtimeEvent realtimeEvent, CancellationToken cancellationToken = default)
        {
            calls.Add("publish");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPublisher : ITaskWorkerRealtimeEventPublisher
    {
        public Guid AttemptedEventId { get; private set; }

        public Task PublishAsync(TaskWorkerRealtimeEvent realtimeEvent, CancellationToken cancellationToken = default)
        {
            AttemptedEventId = realtimeEvent.EventId;
            throw new InvalidOperationException("transport unavailable");
        }
    }
}
