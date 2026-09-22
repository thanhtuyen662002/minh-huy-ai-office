using System.Collections.Concurrent;
using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Fail-closed write seam for order/inventory execution. Durable task authority is
/// re-asserted before authorization/audit, and each deterministic preview identity
/// can invoke the ERP writer at most once.
/// </summary>
public sealed class OrderInventoryExecutionService(AuthorizedToolExecutionGate executionGate)
{
    public const string WriteAction = "order.inventory.write";
    private readonly ConcurrentDictionary<string, Lazy<Task<OrderInventoryExecutionResult>>> executions = new(StringComparer.Ordinal);

    public async Task<OrderInventoryExecutionResult> ExecuteAsync(
        OrderInventoryPreview preview,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<CancellationToken, Task<OrderInventoryExecutionResult>> write,
        string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(authorizationRequest);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(write);

        OrderInventoryContract.ValidatePreview(preview);
        AssertTrustedAuthority(preview, authorizationRequest);

        var identity = $"{preview.Authority.TenantId:D}\n{preview.Authority.CompanyId:D}\n{preview.Authority.DataSourceId:D}\n{preview.IdempotencyKey}";
        var execution = executions.GetOrAdd(
            identity,
            _ => new Lazy<Task<OrderInventoryExecutionResult>>(
                () => ExecuteOnceAsync(preview, authorizationRequest, permissions, write, executionId, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await execution.Value;
        }
        catch
        {
            executions.TryRemove(new KeyValuePair<string, Lazy<Task<OrderInventoryExecutionResult>>>(identity, execution));
            throw;
        }
    }

    public static string ResourceFor(Guid dataSourceId)
    {
        if (dataSourceId == Guid.Empty)
            throw new ArgumentException("Data source id is required.", nameof(dataSourceId));
        return $"erp-data-source:{dataSourceId:D}";
    }

    private async Task<OrderInventoryExecutionResult> ExecuteOnceAsync(
        OrderInventoryPreview preview,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<CancellationToken, Task<OrderInventoryExecutionResult>> write,
        string? executionId,
        CancellationToken cancellationToken)
    {
        var result = await executionGate.ExecuteAsync(
            authorizationRequest,
            permissions,
            write,
            executionId,
            cancellationToken);
        OrderInventoryContract.ValidateResult(preview, result);
        return result;
    }

    private static void AssertTrustedAuthority(OrderInventoryPreview preview, ToolAuthorizationRequest request)
    {
        var authority = preview.Authority;
        if (authority.TenantId != request.TenantId || authority.CompanyId != request.CompanyId)
            throw new UnauthorizedAccessException("Order/inventory authority does not match durable task authority.");

        if (!StringComparer.Ordinal.Equals(request.Resource, ResourceFor(authority.DataSourceId))
            || !StringComparer.Ordinal.Equals(request.Action, WriteAction)
            || request.Risk < ToolRiskLevel.High)
            throw new UnauthorizedAccessException("Order/inventory tool scope is not the required explicit write scope.");
    }
}
