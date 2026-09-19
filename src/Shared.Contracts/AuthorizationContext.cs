namespace MinhHuy.AIOffice.Shared.Contracts;

public sealed record AuthorizationContext
{
    private AuthorizationContext(Guid tenantId, Guid companyId, Guid userId)
    {
        TenantId = tenantId;
        CompanyId = companyId;
        UserId = userId;
    }

    public Guid TenantId { get; }

    public Guid CompanyId { get; }

    public Guid UserId { get; }

    public static AuthorizationContext Create(Guid tenantId, Guid companyId, Guid userId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must be non-empty.", nameof(tenantId));
        }

        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("CompanyId must be non-empty.", nameof(companyId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("UserId must be non-empty.", nameof(userId));
        }

        return new AuthorizationContext(tenantId, companyId, userId);
    }
}
