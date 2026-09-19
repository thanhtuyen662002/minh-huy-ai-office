namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class PlatformUserRecord
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public required string IdentityProvider { get; set; }

    public required string Subject { get; set; }

    public required string DisplayName { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CompanyRecord
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public required string Code { get; set; }

    public required string Name { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CompanyMembershipRecord
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid UserId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class RoleAssignmentRecord
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid UserId { get; set; }

    public required string RoleKey { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
