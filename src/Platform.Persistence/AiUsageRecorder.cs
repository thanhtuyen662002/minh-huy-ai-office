using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>Who a model call is charged to: the question's company, asker, data source and purpose.</summary>
public sealed record AiUsageContext(Guid TenantId, Guid CompanyId, Guid? TaskId, Guid? UserId, Guid? DataSourceId, string Purpose)
{
    public string? ExternalUserId { get; init; }
}

/// <summary>
/// Writes one ledger row per model call and checks budgets between calls. It uses its own short-lived context,
/// so a row is committed even when the question later fails and never mixes with the delivery handler's changes.
/// </summary>
public sealed class AiUsageRecorder(DbContextOptions<PlatformDbContext> options, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>Throws <see cref="AiUsageLimitException"/> when the source's customer has spent this month's budget.</summary>
    public async Task RequireBudgetAsync(AiUsageContext context, CancellationToken token)
    {
        if (context.DataSourceId is not { } sourceId) return;
        await using var database = new PlatformDbContext(options);
        var account = await AccountAsync(database, context, sourceId, token);
        if (account is not null)
            await AiUsageAdmission.RequireBudgetAsync(database, context.TenantId, context.CompanyId, account, clock.GetUtcNow(), token);
    }

    public async Task<AiUsageRecord> RecordAsync(AiUsageContext context, string providerId, string model,
        long inputTokens, long cachedInputTokens, long outputTokens, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        var now = clock.GetUtcNow();
        await using var database = new PlatformDbContext(options);
        var account = context.DataSourceId is { } sourceId ? await AccountAsync(database, context, sourceId, token) : null;
        var name = Bounded(model, AiUsageModel.ModelLength);
        var price = AiPricing.Match(await AiPricing.LatestAsync(database, context.TenantId, context.CompanyId, now, token), name);
        var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var row = new AiUsageRecord
        {
            TenantId = context.TenantId,
            CompanyId = context.CompanyId,
            Id = Guid.NewGuid(),
            OccurredAtUtc = now,
            TaskId = context.TaskId,
            UserId = context.UserId,
            ExternalUserId = context.ExternalUserId,
            DataSourceId = context.DataSourceId,
            BillingAccountId = account?.Id,
            Purpose = Bounded(context.Purpose, 64),
            ProviderId = Bounded(providerId, 100),
            Model = name,
            InputTokens = inputTokens,
            CachedInputTokens = cached,
            OutputTokens = outputTokens,
            Cost = price is null ? null : AiPricing.Cost(price, inputTokens, cached, outputTokens),
            PriceId = price?.Id
        };
        database.AiUsageRecords.Add(row);
        await database.SaveChangesAsync(token);
        return row;
    }

    private static async Task<AiBillingAccountRecord?> AccountAsync(PlatformDbContext database, AiUsageContext context, Guid sourceId,
        CancellationToken token) =>
        await (from link in database.AiBillingAccountSources.AsNoTracking()
               join account in database.AiBillingAccounts.AsNoTracking()
                   on new { link.TenantId, link.CompanyId, Id = link.AccountId } equals new { account.TenantId, account.CompanyId, account.Id }
               where link.TenantId == context.TenantId && link.CompanyId == context.CompanyId && link.SourceId == sourceId
               select account).SingleOrDefaultAsync(token);

    private static string Bounded(string? value, int length)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return text.Length > length ? text[..length] : text;
    }
}
