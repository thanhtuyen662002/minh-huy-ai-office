namespace Platform.Persistence;

public enum ToolRiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

public sealed record ToolAuthorizationRequest(
    Guid TenantId,
    Guid CompanyId,
    Guid UserId,
    Guid TaskId,
    string Resource,
    string Action,
    ToolRiskLevel Risk);

public sealed record ToolPermission(
    Guid TenantId,
    Guid CompanyId,
    Guid UserId,
    string Resource,
    string Action,
    ToolRiskLevel MaximumRisk);

public sealed record ToolAuthorizationDecision(bool Allowed, string Reason)
{
    public static ToolAuthorizationDecision Deny(string reason) => new(false, reason);
    public static ToolAuthorizationDecision Allow() => new(true, "allowed");
}

/// <summary>
/// Mechanical, prompt-independent authorization for tool execution.
/// Identity and company scope supplied here must already come from the trusted,
/// server-derived AuthorizationContext boundary.
/// </summary>
public sealed class ToolAuthorizationPolicy
{
    public ToolAuthorizationDecision Authorize(
        ToolAuthorizationRequest request,
        IReadOnlyCollection<ToolPermission> permissions)
    {
        if (request.TenantId == Guid.Empty || request.CompanyId == Guid.Empty ||
            request.UserId == Guid.Empty || request.TaskId == Guid.Empty)
        {
            return ToolAuthorizationDecision.Deny("missing_authority");
        }

        if (string.IsNullOrWhiteSpace(request.Resource) || string.IsNullOrWhiteSpace(request.Action))
        {
            return ToolAuthorizationDecision.Deny("missing_scope");
        }

        var matches = permissions
            .Where(permission =>
                permission.TenantId == request.TenantId &&
                permission.CompanyId == request.CompanyId &&
                permission.UserId == request.UserId &&
                string.Equals(permission.Resource, request.Resource, StringComparison.Ordinal) &&
                string.Equals(permission.Action, request.Action, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length != 1)
        {
            return ToolAuthorizationDecision.Deny(matches.Length == 0 ? "no_permission" : "ambiguous_permission");
        }

        return request.Risk <= matches[0].MaximumRisk
            ? ToolAuthorizationDecision.Allow()
            : ToolAuthorizationDecision.Deny("risk_exceeds_permission");
    }
}
