using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Admits a new question: per-person and per-customer rate limits, then the customer's monthly budget.
/// Runs inside the submission transaction while the company lock is held, so the counts it reads cannot race
/// with another submission for the same company; the admission row it adds commits with the task.
/// </summary>
public static class AiUsageAdmission
{
    /// <summary>Questions one person may send per minute, whichever customer they ask about.</summary>
    public const int UserQuestionsPerMinute = 20;

    public static async Task<AiQuestionAdmissionRecord> AdmitAsync(PlatformDbContext database, AuthorizationContext authority,
        Guid taskId, Guid dataSourceId, DateTimeOffset now, CancellationToken token, string? externalUserId = null)
    {
        var minuteAgo = now.AddMinutes(-1);
        // A web ERP user is a person of their own, even though all of a client's questions share its service user.
        var byUser = await database.AiQuestionAdmissions.AsNoTracking().CountAsync(row => row.TenantId == authority.TenantId
            && row.CompanyId == authority.CompanyId && row.UserId == authority.UserId && row.ExternalUserId == externalUserId
            && row.AdmittedAtUtc > minuteAgo, token);
        if (byUser >= UserQuestionsPerMinute)
            throw new AiUsageLimitException(AiUsageLimitException.UserRate,
                $"Bạn đã hỏi {UserQuestionsPerMinute} câu trong một phút. Đợi một chút rồi gửi lại.");

        var accountId = await database.AiBillingAccountSources.AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.SourceId == dataSourceId)
            .Select(row => (Guid?)row.AccountId).SingleOrDefaultAsync(token);
        if (accountId is { } id)
        {
            var account = await database.AiBillingAccounts.AsNoTracking()
                .SingleAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.Id == id, token);
            if (account.QuestionsPerMinute is { } perMinute)
            {
                var byAccount = await database.AiQuestionAdmissions.AsNoTracking().CountAsync(row => row.TenantId == authority.TenantId
                    && row.CompanyId == authority.CompanyId && row.BillingAccountId == id && row.AdmittedAtUtc > minuteAgo, token);
                if (byAccount >= perMinute)
                    throw new AiUsageLimitException(AiUsageLimitException.AccountRate,
                        $"{account.Name} đang hỏi nhiều hơn {perMinute} câu một phút. Đợi một chút rồi gửi lại.");
            }
            await RequireBudgetAsync(database, authority.TenantId, authority.CompanyId, account, now, token);
        }

        var admission = new AiQuestionAdmissionRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            TaskId = taskId,
            UserId = authority.UserId,
            ExternalUserId = externalUserId,
            DataSourceId = dataSourceId,
            BillingAccountId = accountId,
            AdmittedAtUtc = now
        };
        database.AiQuestionAdmissions.Add(admission);
        return admission;
    }

    /// <summary>Refuses when the customer's spend this month (Vietnam time) has reached its budget.</summary>
    public static async Task RequireBudgetAsync(PlatformDbContext database, Guid tenantId, Guid companyId,
        AiBillingAccountRecord account, DateTimeOffset now, CancellationToken token)
    {
        if (account.MonthlyBudget is not { } budget) return;
        var spent = await MonthSpendAsync(database, tenantId, companyId, account.Id, now, token);
        if (spent >= budget)
            throw new AiUsageLimitException(AiUsageLimitException.Budget,
                $"{account.Name} đã dùng hết hạn mức AI tháng này ({budget:N0} đ). Liên hệ quản trị viên để nâng hạn mức.");
    }

    public static async Task<decimal> MonthSpendAsync(PlatformDbContext database, Guid tenantId, Guid companyId, Guid accountId,
        DateTimeOffset now, CancellationToken token)
    {
        var from = AiBillingCalendar.MonthStart(now);
        return await database.AiUsageRecords.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.CompanyId == companyId && row.BillingAccountId == accountId
                && row.OccurredAtUtc >= from && row.Cost != null)
            .SumAsync(row => row.Cost ?? 0, token);
    }
}
