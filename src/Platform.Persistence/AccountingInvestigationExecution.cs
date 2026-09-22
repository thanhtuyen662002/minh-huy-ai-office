using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Small deterministic execution seam for accounting investigations. Authority comes from
/// the durable task/tool boundary; the ERP request cannot select a different tenant/company.
/// Every investigation is authorized and audited before the deterministic reader is invoked.
/// </summary>
public sealed class AccountingInvestigationExecutionService(AuthorizedToolExecutionGate executionGate)
{
    public const string ReadAction = "investigate.read";

    public async Task<AccountingInvestigationResult> ExecuteAsync(
        AccountingInvestigationRequest request,
        ErpCatalog catalog,
        ToolAuthorizationRequest authorizationRequest,
        IReadOnlyCollection<ToolPermission> permissions,
        Func<CancellationToken, Task<AccountingInvestigationResult>> investigate,
        string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(authorizationRequest);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(investigate);

        request.Validate(catalog);
        AssertTrustedAuthority(request, authorizationRequest);

        var result = await executionGate.ExecuteAsync(
            authorizationRequest,
            permissions,
            investigate,
            executionId,
            cancellationToken);

        return result.Validate(request);
    }

    public static string ResourceFor(string dataSourceId)
    {
        if (string.IsNullOrWhiteSpace(dataSourceId) || dataSourceId != dataSourceId.Trim())
        {
            throw new ArgumentException("Data source id must be non-empty canonical text.", nameof(dataSourceId));
        }

        return $"erp-data-source:{dataSourceId}";
    }

    private static void AssertTrustedAuthority(
        AccountingInvestigationRequest request,
        ToolAuthorizationRequest authorizationRequest)
    {
        if (!Guid.TryParse(request.TenantId, out var tenantId)
            || !Guid.TryParse(request.CompanyId, out var companyId)
            || tenantId != authorizationRequest.TenantId
            || companyId != authorizationRequest.CompanyId)
        {
            throw new UnauthorizedAccessException("Accounting investigation authority does not match durable task authority.");
        }

        if (!StringComparer.Ordinal.Equals(authorizationRequest.Resource, ResourceFor(request.DataSourceId))
            || !StringComparer.Ordinal.Equals(authorizationRequest.Action, ReadAction)
            || authorizationRequest.Risk != ToolRiskLevel.Low)
        {
            throw new UnauthorizedAccessException("Accounting investigation tool scope is not the required read-only ERP scope.");
        }
    }
}
