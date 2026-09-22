using System.Collections.Concurrent;
using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Fail-closed write seam for accounting posting. Authority comes from the durable
/// task/tool boundary, authorization and audit happen before the writer is invoked,
/// and one idempotency key can produce at most one committed posting result.
/// </summary>
public sealed class AccountingPostingExecutionService(AuthorizedToolExecutionGate executionGate)
{
    public const string WriteAction = "accounting.post.write";
    private readonly ConcurrentDictionary<string, Lazy<Task<AccountingPostingExecutionResult>>> executions = new(StringComparer.Ordinal);

    public async Task<AccountingPostingExecutionResult> ExecuteAsync(
        AccountingPostingPreview preview,
        ErpCatalog catalog,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<CancellationToken, Task<AccountingPostingExecutionResult>> post,
        string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(authorizationRequest);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(post);

        preview.Validate(catalog);
        AssertTrustedAuthority(preview, authorizationRequest);

        var identity = $"{preview.TenantId}\n{preview.CompanyId}\n{preview.DataSourceId}\n{preview.IdempotencyKey}";
        var execution = executions.GetOrAdd(
            identity,
            _ => new Lazy<Task<AccountingPostingExecutionResult>>(
                () => ExecuteOnceAsync(preview, authorizationRequest, permissions, post, executionId, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await execution.Value;
        }
        catch
        {
            executions.TryRemove(new KeyValuePair<string, Lazy<Task<AccountingPostingExecutionResult>>>(identity, execution));
            throw;
        }
    }

    public static string ResourceFor(string dataSourceId)
    {
        if (string.IsNullOrWhiteSpace(dataSourceId) || dataSourceId != dataSourceId.Trim())
        {
            throw new ArgumentException("Data source id must be non-empty canonical text.", nameof(dataSourceId));
        }

        return $"erp-data-source:{dataSourceId}";
    }

    private async Task<AccountingPostingExecutionResult> ExecuteOnceAsync(
        AccountingPostingPreview preview,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<CancellationToken, Task<AccountingPostingExecutionResult>> post,
        string? executionId,
        CancellationToken cancellationToken)
    {
        var result = await executionGate.ExecuteAsync(
            authorizationRequest,
            permissions,
            post,
            executionId,
            cancellationToken);

        return result.Validate(preview);
    }

    private static void AssertTrustedAuthority(
        AccountingPostingPreview preview,
        ToolAuthorizationRequest authorizationRequest)
    {
        if (!Guid.TryParse(preview.TenantId, out var tenantId)
            || !Guid.TryParse(preview.CompanyId, out var companyId)
            || tenantId != authorizationRequest.TenantId
            || companyId != authorizationRequest.CompanyId)
        {
            throw new UnauthorizedAccessException("Accounting posting authority does not match durable task authority.");
        }

        if (!StringComparer.Ordinal.Equals(authorizationRequest.Resource, ResourceFor(preview.DataSourceId))
            || !StringComparer.Ordinal.Equals(authorizationRequest.Action, WriteAction)
            || authorizationRequest.Risk < ToolRiskLevel.High)
        {
            throw new UnauthorizedAccessException("Accounting posting tool scope is not the required explicit write scope.");
        }
    }
}
