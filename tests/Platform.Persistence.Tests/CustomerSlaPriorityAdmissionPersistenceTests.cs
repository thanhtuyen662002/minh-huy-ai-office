using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CustomerSlaPriorityAdmissionPersistenceTests
{
    private static readonly CustomerSlaAuthority Authority = new("tenant-a", "company-a", "user-a", 7);
    private static readonly CustomerSlaPolicy Policy = new(
        "sla-standard",
        3,
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["standard"] = 20,
            ["urgent"] = 80,
        },
        80);

    [Fact]
    public async Task AdmitAndEnqueue_PersistsBeforeEnqueueAndUsesAuthoritativePriority()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var service = new CustomerSlaPriorityAdmissionPersistenceService(store);
        var request = new CustomerSlaPriorityAdmissionRequest("admission-1", Authority, Policy, "urgent");

        var result = await service.AdmitAndEnqueueAsync(
            request,
            (evidence, _) =>
            {
                events.Add($"enqueue:{evidence.SchedulerPriority}");
                return Task.CompletedTask;
            });

        Assert.Equal(["persist:80", "enqueue:80"], events);
        Assert.Equal(80, result.Evidence.SchedulerPriority);
        Assert.True(result.Enqueued);
    }

    [Fact]
    public async Task AdmitAndEnqueue_DoesNotEnqueueWhenPersistenceFails()
    {
        var service = new CustomerSlaPriorityAdmissionPersistenceService(new FailingStore());
        var enqueued = false;
        var request = new CustomerSlaPriorityAdmissionRequest("admission-2", Authority, Policy, "standard");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AdmitAndEnqueueAsync(
            request,
            (_, _) =>
            {
                enqueued = true;
                return Task.CompletedTask;
            }));

        Assert.False(enqueued);
    }

    [Fact]
    public async Task AdmitAndEnqueue_ExactReplayIsIdempotent()
    {
        var store = new InMemoryCustomerSlaPriorityAdmissionStore();
        var service = new CustomerSlaPriorityAdmissionPersistenceService(store);
        var request = new CustomerSlaPriorityAdmissionRequest("admission-3", Authority, Policy, "standard");
        var enqueueCount = 0;

        var first = await service.AdmitAndEnqueueAsync(request, Enqueue);
        var replay = await service.AdmitAndEnqueueAsync(request, Enqueue);

        Assert.Equal(first.Evidence, replay.Evidence);
        Assert.Equal(2, enqueueCount);

        Task Enqueue(CustomerSlaPriorityAdmissionEvidence _, CancellationToken __)
        {
            enqueueCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FindAsync_DoesNotReturnEvidenceAcrossAuthorityBoundary()
    {
        var store = new InMemoryCustomerSlaPriorityAdmissionStore();
        var evidence = CustomerSlaPriorityAdmission.Decide(
            new CustomerSlaPriorityAdmissionRequest("admission-isolated", Authority, Policy, "standard"));
        await store.PersistAsync(evidence);

        Assert.Equal(evidence, await store.FindAsync(evidence.AdmissionId, Authority));
        Assert.Null(await store.FindAsync(evidence.AdmissionId, Authority with { TenantId = "tenant-b" }));
        Assert.Null(await store.FindAsync(evidence.AdmissionId, Authority with { CompanyId = "company-b" }));
        Assert.Null(await store.FindAsync(evidence.AdmissionId, Authority with { UserId = "user-b" }));
        Assert.Null(await store.FindAsync(evidence.AdmissionId, Authority with { AuthorityVersion = Authority.AuthorityVersion + 1 }));
    }

    [Fact]
    public async Task AdmitAndEnqueue_RejectsCrossAuthorityAdmissionIdReplayBeforeEnqueue()
    {
        var store = new InMemoryCustomerSlaPriorityAdmissionStore();
        var service = new CustomerSlaPriorityAdmissionPersistenceService(store);
        var original = new CustomerSlaPriorityAdmissionRequest("admission-4", Authority, Policy, "standard");
        await service.AdmitAndEnqueueAsync(original, (_, _) => Task.CompletedTask);
        var enqueued = false;
        var conflicting = original with { Authority = Authority with { TenantId = "tenant-b" } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AdmitAndEnqueueAsync(
            conflicting,
            (_, _) =>
            {
                enqueued = true;
                return Task.CompletedTask;
            }));

        Assert.False(enqueued);
    }

    private sealed class RecordingStore(List<string> events) : ICustomerSlaPriorityAdmissionStore
    {
        public Task<CustomerSlaPriorityAdmissionEvidence?> FindAsync(
            string admissionId,
            CustomerSlaAuthority authority,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CustomerSlaPriorityAdmissionEvidence?>(null);

        public Task<CustomerSlaPriorityAdmissionEvidence> PersistAsync(
            CustomerSlaPriorityAdmissionEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            events.Add($"persist:{evidence.SchedulerPriority}");
            return Task.FromResult(evidence);
        }
    }

    private sealed class FailingStore : ICustomerSlaPriorityAdmissionStore
    {
        public Task<CustomerSlaPriorityAdmissionEvidence?> FindAsync(
            string admissionId,
            CustomerSlaAuthority authority,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CustomerSlaPriorityAdmissionEvidence?>(null);

        public Task<CustomerSlaPriorityAdmissionEvidence> PersistAsync(
            CustomerSlaPriorityAdmissionEvidence evidence,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("persistence unavailable");
    }
}
