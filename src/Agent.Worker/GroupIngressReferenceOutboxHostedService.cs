using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Agent.Worker;

public sealed class GroupIngressReferenceOutboxHostedService(IServiceScopeFactory scopeFactory, GroupExtractionWorkerBinding worker,
    TimeProvider clock, ILogger<GroupIngressReferenceOutboxHostedService> logger) : BackgroundService
{
    private string? afterIdentityHash;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        worker.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PublishPassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Group reference outbox pass is unavailable; durable SQL backlog remains pending."); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private async Task PublishPassAsync(CancellationToken cancellationToken)
    {
        await using var catalogScope = scopeFactory.CreateAsyncScope();
        var database = catalogScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await catalogScope.ServiceProvider.GetRequiredService<GroupIngressPermissionVerifier>().RequireSafeRuntimeAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (now.Offset != TimeSpan.Zero) throw new InvalidOperationException("Group reference clock is not available.");
        var sources = await DueSources(database, worker, afterIdentityHash, now)
            .Select(binding => new { binding.Id, binding.IdentityHash }).Take(32).ToArrayAsync(cancellationToken);
        if (sources.Length == 0) { afterIdentityHash = null; return; }
        foreach (var source in sources)
        {
            // This is only fair catalog traversal. Due times, retry reservations,
            // original references and accepted inbox receipts remain in SQL.
            afterIdentityHash = source.IdentityHash;
            try
            {
                await using var deliveryScope = scopeFactory.CreateAsyncScope();
                await deliveryScope.ServiceProvider.GetRequiredService<GroupIngressOutboxDispatcher>()
                    .PublishNextAsync(new(worker.TenantId, worker.CompanyId, source.Id), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { logger.LogWarning("Group reference publication was not confirmed; durable SQL retry remains pending."); }
        }
    }

    internal static IOrderedQueryable<GroupBindingRecord> DueSources(PlatformDbContext database, GroupExtractionWorkerBinding worker,
        string? after, DateTimeOffset now) => database.GroupBindings.AsNoTracking().Where(binding =>
            binding.TenantId == worker.TenantId && binding.CompanyId == worker.CompanyId && binding.IsEnabled &&
            binding.Role == GroupBindingRole.CustomerSource && (after == null || string.Compare(binding.IdentityHash, after) > 0) &&
            database.GroupServiceGrants.Any(grant => grant.TenantId == worker.TenantId && grant.CompanyId == worker.CompanyId &&
                grant.BindingId == binding.Id && grant.ServiceId == worker.ServiceId && grant.Capability == GroupServiceCapability.Extract &&
                grant.IsEnabled && grant.Version > 0) &&
            database.GroupIngressOutbox.Any(outbox => outbox.TenantId == worker.TenantId && outbox.CompanyId == worker.CompanyId &&
                outbox.BindingId == binding.Id && outbox.AvailableAtUtc <= now &&
                !database.GroupIngressInbox.Any(receipt => receipt.TenantId == outbox.TenantId && receipt.CompanyId == outbox.CompanyId &&
                    receipt.BindingId == outbox.BindingId && receipt.EventId == outbox.Id)))
            .OrderBy(binding => binding.IdentityHash);
}
