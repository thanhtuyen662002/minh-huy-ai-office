using System.Collections.Concurrent;
using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Fail-closed execution seam for deterministic tax/invoice imports. Durable task authority
/// is re-asserted before authorization/audit, connector secrets remain opaque references,
/// and one authoritative import identity can execute at most once per service lifetime.
/// </summary>
public sealed class TaxInvoiceImportExecutionService(AuthorizedToolExecutionGate executionGate)
{
    public const string ImportAction = "tax.invoice.import";
    private readonly ConcurrentDictionary<string, Lazy<Task<TaxInvoiceImportResult>>> executions = new(StringComparer.Ordinal);

    public async Task<TaxInvoiceImportResult> ExecuteAsync(
        TaxInvoiceImportRequest request,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<TaxInvoiceImportRequest, CancellationToken, Task<TaxInvoiceImportResult>> import,
        string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorizationRequest);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(import);

        TaxInvoiceImportContract.ValidateRequest(request);
        AssertTrustedAuthority(request, authorizationRequest);

        var identity = TaxInvoiceImportContract.GetIdempotencyKey(request);
        var execution = executions.GetOrAdd(
            identity,
            _ => new Lazy<Task<TaxInvoiceImportResult>>(
                () => ExecuteOnceAsync(request, authorizationRequest, permissions, import, executionId, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await execution.Value;
        }
        catch
        {
            executions.TryRemove(new KeyValuePair<string, Lazy<Task<TaxInvoiceImportResult>>>(identity, execution));
            throw;
        }
    }

    public static string ResourceFor(Guid dataSourceId)
    {
        if (dataSourceId == Guid.Empty) throw new ArgumentException("Data source id is required.", nameof(dataSourceId));
        return $"erp-data-source:{dataSourceId:D}";
    }

    private async Task<TaxInvoiceImportResult> ExecuteOnceAsync(
        TaxInvoiceImportRequest request,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<TaxInvoiceImportRequest, CancellationToken, Task<TaxInvoiceImportResult>> import,
        string? executionId,
        CancellationToken cancellationToken)
    {
        var result = await executionGate.ExecuteAsync(
            authorizationRequest,
            permissions,
            ct => import(request, ct),
            executionId,
            cancellationToken);

        TaxInvoiceImportContract.ValidateResult(request, result);
        return result;
    }

    private static void AssertTrustedAuthority(TaxInvoiceImportRequest request, ToolAuthorizationRequest authorizationRequest)
    {
        var authority = request.Authority;
        if (authority.TenantId != authorizationRequest.TenantId
            || authority.CompanyId != authorizationRequest.CompanyId
            || !StringComparer.Ordinal.Equals(authorizationRequest.Resource, ResourceFor(authority.DataSourceId))
            || !StringComparer.Ordinal.Equals(authorizationRequest.Action, ImportAction)
            || authorizationRequest.Risk < ToolRiskLevel.High)
        {
            throw new UnauthorizedAccessException("Tax/invoice import authority does not match durable task write authority.");
        }

        if (!StringComparer.Ordinal.Equals(authority.Capability, ImportAction))
        {
            throw new UnauthorizedAccessException("Tax/invoice import capability is not the required explicit import scope.");
        }
    }
}
