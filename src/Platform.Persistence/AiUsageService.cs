using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record AiPriceView(string Model, decimal InputPerMillion, decimal CachedInputPerMillion, decimal OutputPerMillion,
    DateTimeOffset EffectiveFromUtc);
public sealed record AiBillingAccountView(Guid Id, string Name, decimal? MonthlyBudget, int? QuestionsPerMinute, string Version,
    IReadOnlyList<Guid> SourceIds);
public sealed record AiUsageSourceView(Guid Id, string LogicalName);
public sealed record AiUsageSettings(IReadOnlyList<AiPriceView> Prices, IReadOnlyList<AiBillingAccountView> Accounts,
    IReadOnlyList<AiUsageSourceView> Sources, int UserQuestionsPerMinute);

public sealed record AiPriceSaveRequest(string Model, decimal InputPerMillion, decimal CachedInputPerMillion, decimal OutputPerMillion);
public sealed record AiBillingAccountSaveRequest(string Name, decimal? MonthlyBudget, int? QuestionsPerMinute, IReadOnlyList<Guid> SourceIds,
    string? Version);

public sealed record AiUsageTotals(long Questions, long Calls, long InputTokens, long CachedInputTokens, long OutputTokens, decimal Cost,
    long UnpricedCalls);
public sealed record AiUsageAccountRow(Guid? AccountId, string Name, decimal? MonthlyBudget, AiUsageTotals Totals);
public sealed record AiUsageUserRow(Guid? UserId, string Name, AiUsageTotals Totals);
public sealed record AiUsageModelRow(string ProviderId, string Model, AiUsageTotals Totals);
public sealed record AiUsageDayRow(string Date, AiUsageTotals Totals);
public sealed record AiUsageReport(string Month, string Currency, AiUsageTotals Totals, IReadOnlyList<AiUsageAccountRow> Accounts,
    IReadOnlyList<AiUsageUserRow> Users, IReadOnlyList<AiUsageModelRow> Models, IReadOnlyList<AiUsageDayRow> Days);

/// <summary>Administrator view of AI spend: selling prices per model, billed customers and their limits, and the monthly report.</summary>
public sealed class AiUsageService(PlatformDbContext database, IAuthorizationDirectory directory, TimeProvider? timeProvider = null)
{
    public const string Currency = "VND";
    private const string Unassigned = "Chưa gán khách hàng";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private async Task RequireAdminAsync(AuthorizationContext authority, CancellationToken token)
    {
        var current = await directory.ResolveAsync(authority, token);
        if (current is null || current.Context != authority || !current.Roles.Contains("admin", StringComparer.Ordinal))
            throw new UnauthorizedAccessException();
    }

    public async Task<AiUsageSettings> GetSettingsAsync(AuthorizationContext authority, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var prices = await AiPricing.LatestAsync(database, authority.TenantId, authority.CompanyId, DateTimeOffset.MaxValue, token);
        var accounts = await database.AiBillingAccounts.AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId)
            .OrderBy(row => row.Name).ToListAsync(token);
        var links = await database.AiBillingAccountSources.AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId).ToListAsync(token);
        var sources = await database.DataSources.AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId)
            .OrderBy(row => row.LogicalName).Select(row => new AiUsageSourceView(row.Id, row.LogicalName)).Take(1000).ToListAsync(token);
        return new(
            prices.OrderBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
                .Select(row => new AiPriceView(row.Model, row.InputPerMillion, row.CachedInputPerMillion, row.OutputPerMillion, row.EffectiveFromUtc))
                .ToArray(),
            accounts.Select(row => Describe(row, links)).ToArray(),
            sources,
            AiUsageAdmission.UserQuestionsPerMinute);
    }

    /// <summary>Sets a model's selling price from now on. Usage already recorded keeps the price it was charged at.</summary>
    public async Task<AiPriceView> SavePriceAsync(AuthorizationContext authority, AiPriceSaveRequest request, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Model?.Trim() ?? "";
        if (model.Length is 0 or > AiUsageModel.ModelLength || model.Any(char.IsControl)
            || !Price(request.InputPerMillion) || !Price(request.CachedInputPerMillion) || !Price(request.OutputPerMillion))
            throw new ArgumentException("Invalid price.");
        var row = new AiPriceRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            Id = Guid.NewGuid(),
            Model = model,
            InputPerMillion = request.InputPerMillion,
            CachedInputPerMillion = request.CachedInputPerMillion,
            OutputPerMillion = request.OutputPerMillion,
            EffectiveFromUtc = clock.GetUtcNow(),
            CreatedByUserId = authority.UserId
        };
        database.AiPrices.Add(row);
        await database.SaveChangesAsync(token);
        return new(row.Model, row.InputPerMillion, row.CachedInputPerMillion, row.OutputPerMillion, row.EffectiveFromUtc);
    }

    /// <summary>Creates a customer (<paramref name="accountId"/> null) or updates one at its current version. The listed data
    /// sources are billed to it from now on; a source listed here moves away from any other customer.</summary>
    public async Task<AiBillingAccountView> SaveAccountAsync(AuthorizationContext authority, AiBillingAccountSaveRequest request,
        Guid? accountId = null, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? "";
        var sourceIds = request.SourceIds ?? [];
        if (name.Length is 0 or > 200 || name.Any(char.IsControl)
            || request.MonthlyBudget is < 0 or > 1_000_000_000_000m || request.QuestionsPerMinute is < 1 or > 10_000
            || sourceIds.Count > 1000 || sourceIds.Any(id => id == Guid.Empty) || sourceIds.Distinct().Count() != sourceIds.Count
            || (accountId is null) != (request.Version is null))
            throw new ArgumentException("Invalid customer.");
        var known = await database.DataSources.AsNoTracking()
            .CountAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && sourceIds.Contains(row.Id), token);
        if (known != sourceIds.Count) throw new ArgumentException("Unknown data source.");

        AiBillingAccountRecord account;
        if (accountId is { } id)
        {
            account = await database.AiBillingAccounts.SingleOrDefaultAsync(row => row.TenantId == authority.TenantId
                && row.CompanyId == authority.CompanyId && row.Id == id, token) ?? throw new KeyNotFoundException();
            if (account.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) != request.Version)
                throw new DbUpdateConcurrencyException("The customer changed since it was read.");
            account.Version++;
        }
        else
        {
            account = new AiBillingAccountRecord { TenantId = authority.TenantId, CompanyId = authority.CompanyId, Id = Guid.NewGuid() };
            database.AiBillingAccounts.Add(account);
        }
        account.Name = name;
        account.MonthlyBudget = request.MonthlyBudget;
        account.QuestionsPerMinute = request.QuestionsPerMinute;
        account.UpdatedAtUtc = clock.GetUtcNow();

        var links = await database.AiBillingAccountSources
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                && (row.AccountId == account.Id || sourceIds.Contains(row.SourceId)))
            .ToListAsync(token);
        database.AiBillingAccountSources.RemoveRange(links.Where(row => !sourceIds.Contains(row.SourceId)));
        foreach (var sourceId in sourceIds)
        {
            var link = links.SingleOrDefault(row => row.SourceId == sourceId);
            if (link is null)
                database.AiBillingAccountSources.Add(new AiBillingAccountSourceRecord
                { TenantId = authority.TenantId, CompanyId = authority.CompanyId, SourceId = sourceId, AccountId = account.Id });
            else link.AccountId = account.Id;
        }
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException) { database.ChangeTracker.Clear(); throw; }
        return new(account.Id, account.Name, account.MonthlyBudget, account.QuestionsPerMinute,
            account.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceIds.ToArray());
    }

    /// <summary>One month (Vietnam time) of questions, tokens and cost, by customer, person, model and day.</summary>
    public async Task<AiUsageReport> ReportAsync(AuthorizationContext authority, int year, int month, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        if (year is < 2020 or > 2100 || month is < 1 or > 12) throw new ArgumentException("Invalid month.");
        var from = AiBillingCalendar.MonthStart(year, month);
        var to = from.AddMonths(1);
        var (tenant, company) = (authority.TenantId, authority.CompanyId);

        var usage = database.AiUsageRecords.AsNoTracking()
            .Where(row => row.TenantId == tenant && row.CompanyId == company && row.OccurredAtUtc >= from && row.OccurredAtUtc < to);
        var questions = database.AiQuestionAdmissions.AsNoTracking()
            .Where(row => row.TenantId == tenant && row.CompanyId == company && row.AdmittedAtUtc >= from && row.AdmittedAtUtc < to);

        var byAccount = await usage.GroupBy(row => row.BillingAccountId).Select(group => new UsageSum<Guid?>(group.Key, group.LongCount(),
            group.Sum(row => row.InputTokens), group.Sum(row => row.CachedInputTokens), group.Sum(row => row.OutputTokens),
            group.Sum(row => row.Cost ?? 0), group.LongCount(row => row.Cost == null))).ToListAsync(token);
        var byUser = await usage.GroupBy(row => row.UserId).Select(group => new UsageSum<Guid?>(group.Key, group.LongCount(),
            group.Sum(row => row.InputTokens), group.Sum(row => row.CachedInputTokens), group.Sum(row => row.OutputTokens),
            group.Sum(row => row.Cost ?? 0), group.LongCount(row => row.Cost == null))).ToListAsync(token);
        var byModel = await usage.GroupBy(row => new { row.ProviderId, row.Model }).Select(group => new
        {
            group.Key.ProviderId,
            group.Key.Model,
            Calls = group.LongCount(),
            Input = group.Sum(row => row.InputTokens),
            Cached = group.Sum(row => row.CachedInputTokens),
            Output = group.Sum(row => row.OutputTokens),
            Cost = group.Sum(row => row.Cost ?? 0),
            Unpriced = group.LongCount(row => row.Cost == null)
        }).ToListAsync(token);
        var questionsByAccount = await questions.GroupBy(row => row.BillingAccountId)
            .Select(group => new { group.Key, Count = group.LongCount() }).ToDictionaryAsync(row => row.Key ?? Guid.Empty, row => row.Count, token);
        var questionsByUser = await questions.GroupBy(row => row.UserId)
            .Select(group => new { group.Key, Count = group.LongCount() }).ToDictionaryAsync(row => row.Key, row => row.Count, token);

        // Days are bucketed in Vietnam time from a narrow projection (one month of rows).
        var dayRows = await usage.Select(row => new { row.OccurredAtUtc, row.InputTokens, row.CachedInputTokens, row.OutputTokens, row.Cost })
            .ToListAsync(token);
        var dayQuestions = await questions.Select(row => row.AdmittedAtUtc).ToListAsync(token);
        static string Day(DateTimeOffset instant) => instant.ToOffset(AiBillingCalendar.Offset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var questionDays = dayQuestions.GroupBy(Day).ToDictionary(group => group.Key, group => (long)group.Count());
        var days = dayRows.GroupBy(row => Day(row.OccurredAtUtc))
            .Select(group => group.Key).Union(questionDays.Keys).Order(StringComparer.Ordinal)
            .Select(day =>
            {
                var rows = dayRows.Where(row => Day(row.OccurredAtUtc) == day).ToArray();
                return new AiUsageDayRow(day, new AiUsageTotals(questionDays.GetValueOrDefault(day), rows.Length,
                    rows.Sum(row => row.InputTokens), rows.Sum(row => row.CachedInputTokens), rows.Sum(row => row.OutputTokens),
                    rows.Sum(row => row.Cost ?? 0), rows.LongCount(row => row.Cost == null)));
            }).ToArray();

        var accounts = await database.AiBillingAccounts.AsNoTracking()
            .Where(row => row.TenantId == tenant && row.CompanyId == company).ToListAsync(token);
        var accountRows = accounts.Select(account => (Guid?)account.Id).Concat(byAccount.Select(row => row.Key))
            .Concat(questionsByAccount.Keys.Select(key => key == Guid.Empty ? null : (Guid?)key)).Distinct()
            .Select(id =>
            {
                var account = accounts.SingleOrDefault(row => row.Id == id);
                var sum = byAccount.SingleOrDefault(row => row.Key == id);
                return new AiUsageAccountRow(id, account?.Name ?? Unassigned, account?.MonthlyBudget,
                    Totals(sum, questionsByAccount.GetValueOrDefault(id ?? Guid.Empty)));
            })
            .Where(row => row.AccountId is not null || row.Totals.Calls > 0 || row.Totals.Questions > 0)
            .OrderByDescending(row => row.Totals.Cost).ThenBy(row => row.Name, StringComparer.CurrentCulture).ToArray();

        var userIds = byUser.Select(row => row.Key).OfType<Guid>().Concat(questionsByUser.Keys).Distinct().ToArray();
        var names = await database.Users.AsNoTracking().Where(row => row.TenantId == tenant && userIds.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, row => row.DisplayName, token);
        var userRows = byUser.Select(row => row.Key).Concat(questionsByUser.Keys.Select(key => (Guid?)key)).Distinct()
            .Select(id => new AiUsageUserRow(id, id is { } known ? names.GetValueOrDefault(known, "Người dùng đã xoá") : "Hệ thống",
                Totals(byUser.SingleOrDefault(row => row.Key == id), id is { } user ? questionsByUser.GetValueOrDefault(user) : 0)))
            .OrderByDescending(row => row.Totals.Cost).ThenByDescending(row => row.Totals.InputTokens + row.Totals.OutputTokens).ToArray();

        var modelRows = byModel.Select(row => new AiUsageModelRow(row.ProviderId, row.Model,
                new AiUsageTotals(0, row.Calls, row.Input, row.Cached, row.Output, row.Cost, row.Unpriced)))
            .OrderByDescending(row => row.Totals.Cost).ToArray();
        var total = new AiUsageTotals(await questions.LongCountAsync(token), byModel.Sum(row => row.Calls), byModel.Sum(row => row.Input),
            byModel.Sum(row => row.Cached), byModel.Sum(row => row.Output), byModel.Sum(row => row.Cost), byModel.Sum(row => row.Unpriced));
        return new($"{year:D4}-{month:D2}", Currency, total, accountRows, userRows, modelRows, days);
    }

    private static AiUsageTotals Totals<T>(UsageSum<T>? sum, long questions) => sum is null
        ? new(questions, 0, 0, 0, 0, 0, 0)
        : new(questions, sum.Calls, sum.Input, sum.Cached, sum.Output, sum.Cost, sum.Unpriced);

    private static AiBillingAccountView Describe(AiBillingAccountRecord row, IEnumerable<AiBillingAccountSourceRecord> links) =>
        new(row.Id, row.Name, row.MonthlyBudget, row.QuestionsPerMinute, row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            links.Where(link => link.AccountId == row.Id).Select(link => link.SourceId).ToArray());

    private static bool Price(decimal value) => value is >= 0 and <= 1_000_000_000m && decimal.Round(value, 4) == value;

    private sealed record UsageSum<T>(T Key, long Calls, long Input, long Cached, long Output, decimal Cost, long Unpriced);
}
