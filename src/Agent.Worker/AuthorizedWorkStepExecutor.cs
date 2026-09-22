using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>
/// Supplies explicit server-trusted tool scope for a durable work step. Implementations must not
/// derive resource/action/risk from broker payloads or prompt text.
/// </summary>
public interface ITrustedToolExecutionMetadataProvider
{
    Task<TrustedToolExecutionMetadata> GetAsync(
        WorkDispatchEnvelope envelope,
        WorkLeaseSnapshot lease,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves permissions from the authoritative server-side policy store for the already-derived
/// authorization request. Absence or ambiguity is denied by <see cref="ToolAuthorizationPolicy"/>.
/// </summary>
public interface IToolPermissionProvider
{
    Task<IReadOnlyCollection<ToolPermission>> GetAsync(
        ToolAuthorizationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Mandatory prompt-independent authorization decorator for the worker's actual execution boundary.
/// Durable task authority supplies the user identity; explicit trusted metadata supplies tool scope;
/// every attempt is audited before the inner executor can run.
/// </summary>
public sealed class AuthorizedWorkStepExecutor(
    IRawWorkStepExecutor inner,
    ITrustedToolExecutionMetadataProvider metadataProvider,
    IToolPermissionProvider permissionProvider,
    TrustedToolAuthorizationRequestFactory requestFactory,
    AuthorizedToolExecutionGate gate) : IWorkStepExecutor
{
    public async Task<WorkStepExecutionResult> ExecuteAsync(
        WorkDispatchEnvelope envelope,
        WorkLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);

        var metadata = await metadataProvider.GetAsync(envelope, lease, cancellationToken)
            ?? throw new UnauthorizedAccessException("Trusted tool execution metadata is missing.");

        var request = await requestFactory.CreateAsync(
            envelope.TenantId,
            envelope.CompanyId,
            envelope.TaskId,
            metadata,
            cancellationToken);

        var permissions = await permissionProvider.GetAsync(request, cancellationToken)
            ?? throw new UnauthorizedAccessException("Tool permission source returned no result.");

        return await gate.ExecuteAsync(
            request,
            permissions,
            ct => inner.ExecuteAsync(envelope, lease, ct),
            envelope.MessageId.ToString("N"),
            cancellationToken);
    }
}

/// <summary>
/// Internal marker separating the raw side-effecting executor from the authorized public boundary.
/// DI exposes only <see cref="IWorkStepExecutor"/> to the delivery handler.
/// </summary>
public interface IRawWorkStepExecutor : IWorkStepExecutor
{
}
