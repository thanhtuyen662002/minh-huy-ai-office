using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public sealed record TrustedToolExecutionMetadata(string Resource, string Action, ToolRiskLevel Risk);

/// <summary>
/// Builds tool authorization requests from durable server-side task authority plus explicit
/// trusted execution metadata. Broker/caller identity is never accepted as user authority.
/// Missing, ambiguous, or cross-company task authority fails closed.
/// </summary>
public sealed class TrustedToolAuthorizationRequestFactory(PlatformDbContext dbContext)
{
    public async Task<ToolAuthorizationRequest> CreateAsync(
        Guid tenantId,
        Guid companyId,
        Guid taskId,
        TrustedToolExecutionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (tenantId == Guid.Empty || companyId == Guid.Empty || taskId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Tool execution authority is incomplete.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Resource) || string.IsNullOrWhiteSpace(metadata.Action))
        {
            throw new UnauthorizedAccessException("Tool execution scope is incomplete.");
        }

        var tasks = await dbContext.Tasks
            .AsNoTracking()
            .Where(task => task.TenantId == tenantId && task.CompanyId == companyId && task.Id == taskId)
            .Select(task => task.CreatedByUserId)
            .Take(2)
            .ToArrayAsync(cancellationToken);

        if (tasks.Length != 1 || tasks[0] == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Durable task authority is missing or ambiguous.");
        }

        return new ToolAuthorizationRequest(
            tenantId,
            companyId,
            tasks[0],
            taskId,
            metadata.Resource.Trim(),
            metadata.Action.Trim(),
            metadata.Risk);
    }
}
