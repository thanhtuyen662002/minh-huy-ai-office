using System.Collections.Concurrent;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Durable-boundary abstraction for SLA priority admission evidence. Implementations must
/// commit evidence before returning; scheduler enqueue is deliberately a separate callback.
/// Reads are authority-scoped so AdmissionId alone never grants cross-tenant evidence access.
/// </summary>
public interface ICustomerSlaPriorityAdmissionStore
{
    Task<CustomerSlaPriorityAdmissionEvidence?> FindAsync(
        string admissionId,
        CustomerSlaAuthority authority,
        CancellationToken cancellationToken = default);

    Task<CustomerSlaPriorityAdmissionEvidence> PersistAsync(
        CustomerSlaPriorityAdmissionEvidence evidence,
        CancellationToken cancellationToken = default);
}

public sealed record CustomerSlaScheduledAdmission(
    CustomerSlaPriorityAdmissionEvidence Evidence,
    bool Enqueued);

/// <summary>
/// Enforces decide -> durable evidence -> enqueue ordering. The caller supplies only the
/// requested service class; numeric scheduler priority is always derived from authoritative
/// SLA policy and is never accepted as caller authority.
/// </summary>
public sealed class CustomerSlaPriorityAdmissionPersistenceService(
    ICustomerSlaPriorityAdmissionStore store)
{
    public async Task<CustomerSlaScheduledAdmission> AdmitAndEnqueueAsync(
        CustomerSlaPriorityAdmissionRequest request,
        Func<CustomerSlaPriorityAdmissionEvidence, CancellationToken, Task> enqueue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(enqueue);

        var existing = await store.FindAsync(request.AdmissionId, request.Authority, cancellationToken);
        var decided = CustomerSlaPriorityAdmission.Decide(request, existing);
        var persisted = await store.PersistAsync(decided, cancellationToken);

        if (persisted != decided)
        {
            throw new InvalidOperationException(
                "Persisted SLA admission evidence conflicts with the authoritative decision.");
        }

        await enqueue(persisted, cancellationToken);
        return new CustomerSlaScheduledAdmission(persisted, true);
    }
}

/// <summary>
/// Process-local implementation used by composition/tests until the SQL-backed admission
/// repository is wired. It preserves canonical idempotency and rejects conflicting replay.
/// </summary>
public sealed class InMemoryCustomerSlaPriorityAdmissionStore : ICustomerSlaPriorityAdmissionStore
{
    private readonly ConcurrentDictionary<string, CustomerSlaPriorityAdmissionEvidence> evidence =
        new(StringComparer.Ordinal);

    public Task<CustomerSlaPriorityAdmissionEvidence?> FindAsync(
        string admissionId,
        CustomerSlaAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        cancellationToken.ThrowIfCancellationRequested();
        evidence.TryGetValue(admissionId, out var existing);
        return Task.FromResult(existing is not null && existing.Authority == authority ? existing : null);
    }

    public Task<CustomerSlaPriorityAdmissionEvidence> PersistAsync(
        CustomerSlaPriorityAdmissionEvidence value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        var persisted = evidence.AddOrUpdate(
            value.AdmissionId,
            value,
            (_, existing) => existing == value
                ? existing
                : throw new InvalidOperationException(
                    "Admission identity already has conflicting persisted evidence."));

        return Task.FromResult(persisted);
    }
}
