using System.Collections.Concurrent;
using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Fail-closed execution seam for deterministic ERP bulk writes. The caller may plan
/// many items, but every side effect is independently authority checked, audited and
/// fenced by its exact versioned item identity.
/// </summary>
public sealed class BulkDeterministicExecutionService(AuthorizedToolExecutionGate executionGate)
{
    public const string WriteAction = "erp.bulk.write";
    private readonly ConcurrentDictionary<string, Lazy<Task<BulkExecutionItemResult>>> executions = new(StringComparer.Ordinal);

    public async Task<BulkExecutionItemResult> ExecuteItemAsync(
        BulkExecutionPlan plan,
        BulkExecutionItem item,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<CancellationToken, Task<BulkExecutionItemResult>> write,
        string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(authorizationRequest);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(write);

        BulkDeterministicExecutionContract.ValidatePlan(plan);
        if (!plan.Items.Any(candidate => StringComparer.Ordinal.Equals(candidate.GetExecutionIdentity(plan.Authority), item.GetExecutionIdentity(plan.Authority))))
            throw new UnauthorizedAccessException("Bulk item is not part of the authoritative execution plan.");
        AssertTrustedAuthority(plan, authorizationRequest);

        var identity = item.GetExecutionIdentity(plan.Authority);
        var execution = executions.GetOrAdd(
            identity,
            _ => new Lazy<Task<BulkExecutionItemResult>>(
                () => ExecuteOnceAsync(plan, item, authorizationRequest, permissions, write, executionId, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await execution.Value;
        }
        catch
        {
            executions.TryRemove(new KeyValuePair<string, Lazy<Task<BulkExecutionItemResult>>>(identity, execution));
            throw;
        }
    }

    public static string ResourceFor(Guid dataSourceId)
    {
        if (dataSourceId == Guid.Empty)
            throw new ArgumentException("Data source id is required.", nameof(dataSourceId));
        return $"erp-data-source:{dataSourceId:D}";
    }

    private async Task<BulkExecutionItemResult> ExecuteOnceAsync(
        BulkExecutionPlan plan,
        BulkExecutionItem item,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<CancellationToken, Task<BulkExecutionItemResult>> write,
        string? executionId,
        CancellationToken cancellationToken)
    {
        var result = await executionGate.ExecuteAsync(
            authorizationRequest,
            permissions,
            write,
            executionId,
            cancellationToken);
        BulkDeterministicExecutionContract.ValidateResult(plan, item, result);
        return result;
    }

    private static void AssertTrustedAuthority(BulkExecutionPlan plan, ToolAuthorizationRequest request)
    {
        var authority = plan.Authority;
        if (authority.TenantId != request.TenantId || authority.CompanyId != request.CompanyId)
            throw new UnauthorizedAccessException("Bulk execution authority does not match durable task authority.");
        if (!StringComparer.Ordinal.Equals(request.Resource, ResourceFor(authority.DataSourceId))
            || !StringComparer.Ordinal.Equals(request.Action, WriteAction)
            || request.Risk < ToolRiskLevel.High)
            throw new UnauthorizedAccessException("Bulk execution tool scope is not the required explicit high-risk write scope.");
    }
}
