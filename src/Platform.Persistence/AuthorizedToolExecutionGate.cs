namespace Platform.Persistence;

/// <summary>
/// Prompt-independent execution boundary. Every attempted tool action is authorized and
/// audited before the executor is invoked; denied requests never reach the executor.
/// </summary>
public sealed class AuthorizedToolExecutionGate(
    ToolAuthorizationPolicy policy,
    MinhHuy.AIOffice.Platform.Persistence.ToolExecutionAuditService audit)
{
    public async Task<T> ExecuteAsync<T>(
        ToolAuthorizationRequest request,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<CancellationToken, Task<T>> execute,
        string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(execute);

        var decision = policy.Authorize(request, permissions);
        await audit.RecordAsync(request, decision, DateTimeOffset.UtcNow, executionId, cancellationToken);

        if (!decision.Allowed)
        {
            throw new UnauthorizedAccessException($"Tool execution denied: {decision.Reason}.");
        }

        return await execute(cancellationToken);
    }
}
