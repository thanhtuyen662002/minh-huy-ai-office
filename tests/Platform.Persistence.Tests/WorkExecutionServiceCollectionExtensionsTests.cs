extern alias RuntimeWorker;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class WorkExecutionServiceCollectionExtensionsTests
{
    [Fact]
    public void AddDurableRabbitMqWorkExecution_ExposesOnlyAuthorizedExecutorBoundary()
    {
        var services = new ServiceCollection();
        services.AddDurableRabbitMqWorkExecution<TestExecutor, TestMetadataProvider, TestPermissionProvider>(
            options => options.UseInMemoryDatabase($"worker-di-{Guid.NewGuid():N}"),
            options =>
            {
                options.HostName = "rabbitmq";
                options.QueueName = "minhhuy.work.test";
                options.PrefetchCount = 4;
            });

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IWorkStepExecutor) && descriptor.ImplementationType == typeof(AuthorizedWorkStepExecutor) && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IWorkStepExecutor) && descriptor.ImplementationType == typeof(TestExecutor));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IRawWorkStepExecutor) && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IToolExecutionAuditSink) && descriptor.ImplementationType == typeof(SqlToolExecutionAuditSink) && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IWorkDeliveryHandler) && descriptor.ImplementationType == typeof(PersistentWorkDeliveryHandler) && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IWorkPublisher) && descriptor.ImplementationType == typeof(RabbitMqWorkPublisher) && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(RabbitMqWorkConsumer));
    }

    private sealed class TestExecutor : IRawWorkStepExecutor
    {
        public Task<WorkStepExecutionResult> ExecuteAsync(WorkDispatchEnvelope envelope, WorkLeaseSnapshot lease, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestMetadataProvider : ITrustedToolExecutionMetadataProvider
    {
        public Task<TrustedToolExecutionMetadata> GetAsync(WorkDispatchEnvelope envelope, WorkLeaseSnapshot lease, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestPermissionProvider : IToolPermissionProvider
    {
        public Task<IReadOnlyCollection<ToolPermission>> GetAsync(ToolAuthorizationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
