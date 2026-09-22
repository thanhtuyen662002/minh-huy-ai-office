using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaskWorkerRealtimeEventProjectorTests
{
    private readonly TaskWorkerRealtimeEventProjector projector = new();

    [Theory]
    [InlineData("task.status.changed", "{\"status\":\"Running\"}", TaskWorkerEventKind.TaskStatusChanged)]
    [InlineData("task.approval.required", "{}", TaskWorkerEventKind.ApprovalRequired)]
    [InlineData("task.blocked", "{}", TaskWorkerEventKind.Blocked)]
    public void Project_TaskEvents_PreserveDurableScopeAndSequence(string eventType, string payload, TaskWorkerEventKind kind)
    {
        var source = CreateEvent(eventType, payload);

        var projected = projector.Project(source);

        Assert.Equal(kind, projected.Kind);
        Assert.Equal(source.TenantId, projected.TenantId);
        Assert.Equal(source.CompanyId, projected.CompanyId);
        Assert.Equal(source.TaskId, projected.TaskId);
        Assert.Equal(source.Sequence, projected.Sequence);
        Assert.Equal(source.OccurredAtUtc, projected.OccurredAtUtc);
    }

    [Fact]
    public void Project_StepStatus_RequiresDurableStepScope()
    {
        var source = CreateEvent("step.status.changed", "{\"status\":\"Running\"}");
        source.StepId = Guid.NewGuid();

        var projected = projector.Project(source);

        Assert.Equal(TaskWorkerEventKind.StepStatusChanged, projected.Kind);
        Assert.Equal(source.StepId, projected.StepId);
        Assert.Equal(TaskStepStatus.Running, projected.StepStatus);
    }

    [Fact]
    public void Project_WorkerStatus_ExposesOnlyIdentifierAndStatus()
    {
        var source = CreateEvent("worker.status.changed", "{\"workerId\":\"worker-7\",\"status\":\"Busy\",\"secret\":\"must-not-project\"}");

        var projected = projector.Project(source);

        Assert.Equal(TaskWorkerEventKind.WorkerStatusChanged, projected.Kind);
        Assert.Equal("worker-7", projected.WorkerId);
        Assert.Equal(WorkerExecutionStatus.Busy, projected.WorkerStatus);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(projected), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_Redelivery_IsDeterministicAndIdempotent()
    {
        var source = CreateEvent("task.status.changed", "{\"status\":\"Completed\"}");

        var first = projector.Project(source);
        var redelivery = projector.Project(source);

        Assert.Equal(first, redelivery);
        Assert.Equal(first.EventId, redelivery.EventId);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("task")]
    public void Project_MissingAuthorityScope_FailsClosed(string missing)
    {
        var source = CreateEvent("task.status.changed", "{\"status\":\"Running\"}");
        if (missing == "tenant") source.TenantId = Guid.Empty;
        if (missing == "company") source.CompanyId = Guid.Empty;
        if (missing == "task") source.TaskId = Guid.Empty;

        Assert.Throws<InvalidOperationException>(() => projector.Project(source));
    }

    [Fact]
    public void Project_UnsupportedEventOrPayload_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => projector.Project(CreateEvent("task.unknown", "{}")));
        Assert.Throws<InvalidOperationException>(() => projector.Project(CreateEvent("task.status.changed", "{\"status\":\"NotAStatus\"}")));
    }

    private static TaskEventRecord CreateEvent(string eventType, string payload) => new()
    {
        TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        TaskId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Sequence = 17,
        EventType = eventType,
        PayloadJson = payload,
        OccurredAtUtc = DateTimeOffset.Parse("2026-09-21T10:00:00Z")
    };
}
