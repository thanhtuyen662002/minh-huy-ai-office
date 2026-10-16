using System.Text.Json;
using Setup.Core;
using Xunit;

namespace Setup.Core.Tests;

public sealed class RuntimeReadinessTests
{
    private static readonly string[] Services = ["sql", "identity-db", "identity", "rabbitmq", "redis",
        "jaeger", "otel-collector", "core-api", "agent-worker", "web", "bootstrap"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrayAndLineFormatsRequireAllServicesAndSuccessfulBootstrap(bool lines)
    {
        var entries = Services.Select(service => Entry(service)).ToArray();
        var output = lines ? string.Join("\n", entries.Select(item => JsonSerializer.Serialize(item))) : JsonSerializer.Serialize(entries);
        Assert.True(RuntimeReadiness.IsReady(output));
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("identity-db")]
    [InlineData("identity")]
    [InlineData("rabbitmq")]
    [InlineData("redis")]
    [InlineData("jaeger")]
    [InlineData("otel-collector")]
    [InlineData("core-api")]
    [InlineData("agent-worker")]
    [InlineData("web")]
    [InlineData("bootstrap")]
    public void FailedOrMissingServiceCannotBeHiddenByHealthyWeb(string failed)
    {
        Assert.False(RuntimeReadiness.IsReady(JsonSerializer.Serialize(Services.Where(service => service != failed).Select(service => Entry(service)))));
        Assert.False(RuntimeReadiness.IsReady(JsonSerializer.Serialize(Services.Select(service => Entry(service,
            service == failed ? "restarting" : null)))));
    }

    [Fact]
    public void BootstrapFailureAndUnhealthyDatabasesAreNotReady()
    {
        Assert.False(RuntimeReadiness.IsReady(JsonSerializer.Serialize(Services.Select(service => Entry(service, exitCode: service == "bootstrap" ? 1 : 0)))));
        Assert.False(RuntimeReadiness.IsReady(JsonSerializer.Serialize(Services.Select(service => Entry(service, health: service == "sql" ? "unhealthy" : "")))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[{\"Service\":null,\"State\":\"running\",\"ExitCode\":0}]")]
    public void MalformedOrEmptyRuntimeStatusFailsClosed(string status)
    {
        Assert.False(RuntimeReadiness.IsReady(status));
    }

    [Fact]
    public void DuplicateServiceCannotMaskAnUnhealthyContainer()
    {
        var entries = Services.Select(service => Entry(service)).Append(Entry("sql", health: "unhealthy"));
        Assert.False(RuntimeReadiness.IsReady(JsonSerializer.Serialize(entries)));
    }

    private static object Entry(string service, string? state = null, int exitCode = 0, string health = "")
        => new { Service = service, State = state ?? (service == "bootstrap" ? "exited" : "running"), ExitCode = exitCode, Health = health };
}
