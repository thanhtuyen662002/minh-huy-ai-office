using System.Diagnostics;
using MinhHuy.AIOffice.Platform.Observability;

namespace MinhHuy.AIOffice.Platform.Observability.Tests;

public sealed class TelemetryCorrelationContextTests
{
    [Fact]
    public void Context_applies_safe_task_company_and_trace_dimensions()
    {
        using var activity = new Activity("test").Start();
        var context = TelemetryCorrelationContext.Create(
            "task-123",
            "company:456",
            activity);

        context.ApplyTo(activity);

        Assert.Equal("task-123", activity.GetTagItem(TelemetryDimensions.TaskId));
        Assert.Equal("company:456", activity.GetTagItem(TelemetryDimensions.CompanyId));
        Assert.Equal(activity.TraceId.ToString(), activity.GetTagItem(TelemetryDimensions.TraceId));

        var scope = context.ToLogScope();
        Assert.Equal("task-123", scope["TaskId"]);
        Assert.Equal("company:456", scope["CompanyId"]);
        Assert.Equal(activity.TraceId.ToString(), scope["TraceId"]);
    }

    [Theory]
    [InlineData("contains whitespace")]
    [InlineData("contains/slash")]
    [InlineData("contains\nnewline")]
    public void Context_rejects_unsafe_identifiers(string value)
    {
        Assert.False(
            TelemetryCorrelationContext.TryCreate(
                value,
                "company-1",
                activity: null,
                out _));
    }

    [Fact]
    public void Context_rejects_identifiers_over_the_length_limit()
    {
        var value = new string('a', TelemetryCorrelationContext.MaximumIdentifierLength + 1);

        Assert.Throws<FormatException>(
            () => TelemetryCorrelationContext.Create(value, null));
    }

    [Fact]
    public void Missing_optional_business_dimensions_are_valid()
    {
        Assert.True(
            TelemetryCorrelationContext.TryCreate(
                null,
                null,
                activity: null,
                out var context));

        Assert.Null(context.TaskId);
        Assert.Null(context.CompanyId);
        Assert.Equal(string.Empty, context.TraceId);
    }
}
