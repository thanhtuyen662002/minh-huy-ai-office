using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Selling price per million tokens (VND) for one model, as of <see cref="EffectiveFromUtc"/>. Rows are never edited:
/// a price change adds a row, so usage already costed keeps the price it was charged at.
/// </summary>
public sealed class AiPriceRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string Model { get; set; } = "";
    public decimal InputPerMillion { get; set; }
    public decimal CachedInputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
}

/// <summary>A customer that is billed for AI use: a name, an optional monthly budget (VND) and question rate.</summary>
public sealed class AiBillingAccountRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public decimal? MonthlyBudget { get; set; }
    public int? QuestionsPerMinute { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Which customer pays for questions asked against a data source (one account per source).</summary>
public sealed class AiBillingAccountSourceRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid SourceId { get; set; }
    public Guid AccountId { get; set; }
}

/// <summary>One accepted question, written in the submission transaction; the basis for rate limits and question counts.</summary>
public sealed class AiQuestionAdmissionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }
    public string? ExternalUserId { get; set; }
    public Guid DataSourceId { get; set; }
    public Guid? BillingAccountId { get; set; }
    public DateTimeOffset AdmittedAtUtc { get; set; }
}

/// <summary>
/// One model call and what it cost. Append-only: the runtime may insert and read, never change or delete.
/// <see cref="Cost"/> is null when no price was set for the model at the time (the tokens are still recorded).
/// </summary>
public sealed class AiUsageRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? UserId { get; set; }
    public string? ExternalUserId { get; set; }
    public Guid? DataSourceId { get; set; }
    public Guid? BillingAccountId { get; set; }
    public string Purpose { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal? Cost { get; set; }
    public Guid? PriceId { get; set; }
}

public static class AiUsageModel
{
    public const int ModelLength = 200;

    public static void ConfigureAiUsage(this ModelBuilder model)
    {
        model.Entity<AiPriceRecord>(entity =>
        {
            entity.ToTable("AiPrices", table => table.HasCheckConstraint("CK_AiPrices_NonNegative",
                "[InputPerMillion] >= 0 AND [CachedInputPerMillion] >= 0 AND [OutputPerMillion] >= 0"));
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.Model, row.EffectiveFromUtc }).IsUnique();
            entity.Property(row => row.Model).HasMaxLength(ModelLength);
            entity.Property(row => row.InputPerMillion).HasPrecision(18, 4);
            entity.Property(row => row.CachedInputPerMillion).HasPrecision(18, 4);
            entity.Property(row => row.OutputPerMillion).HasPrecision(18, 4);
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.CompanyId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AiBillingAccountRecord>(entity =>
        {
            entity.ToTable("AiBillingAccounts", table =>
            {
                table.HasCheckConstraint("CK_AiBillingAccounts_Version", "[Version] > 0");
                table.HasCheckConstraint("CK_AiBillingAccounts_Limits",
                    "([MonthlyBudget] IS NULL OR [MonthlyBudget] >= 0) AND ([QuestionsPerMinute] IS NULL OR [QuestionsPerMinute] BETWEEN 1 AND 10000)");
            });
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.Name }).IsUnique();
            entity.Property(row => row.Name).HasMaxLength(200);
            entity.Property(row => row.MonthlyBudget).HasPrecision(18, 2);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.CompanyId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AiBillingAccountSourceRecord>(entity =>
        {
            entity.ToTable("AiBillingAccountSources");
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.SourceId });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.AccountId });
            entity.HasOne<DataSourceRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, row.SourceId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AiBillingAccountRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, row.AccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AiQuestionAdmissionRecord>(entity =>
        {
            entity.ToTable("AiQuestionAdmissions");
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.TaskId });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.AdmittedAtUtc });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.UserId, row.AdmittedAtUtc });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.BillingAccountId, row.AdmittedAtUtc });
            entity.Property(row => row.ExternalUserId).HasMaxLength(200);
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.CompanyId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AiUsageRecord>(entity =>
        {
            entity.ToTable("AiUsageRecords", table => table.HasCheckConstraint("CK_AiUsageRecords_NonNegative",
                "[InputTokens] >= 0 AND [CachedInputTokens] >= 0 AND [CachedInputTokens] <= [InputTokens] AND [OutputTokens] >= 0 AND ([Cost] IS NULL OR [Cost] >= 0)"));
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.OccurredAtUtc });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.BillingAccountId, row.OccurredAtUtc });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.TaskId });
            entity.Property(row => row.ExternalUserId).HasMaxLength(200);
            entity.Property(row => row.Purpose).HasMaxLength(64);
            entity.Property(row => row.ProviderId).HasMaxLength(100);
            entity.Property(row => row.Model).HasMaxLength(ModelLength);
            entity.Property(row => row.Cost).HasPrecision(18, 4);
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.CompanyId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Billing months follow Vietnam time (UTC+7, no daylight saving).</summary>
public static class AiBillingCalendar
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(7);

    public static DateTimeOffset MonthStart(DateTimeOffset instant)
    {
        var local = instant.ToOffset(Offset);
        return new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, Offset);
    }

    public static DateTimeOffset MonthStart(int year, int month) => new(year, month, 1, 0, 0, 0, Offset);
}

/// <summary>A question was refused before it reached the model: too many questions or the customer's budget is spent.</summary>
public sealed class AiUsageLimitException(string code, string message) : Exception(message)
{
    public const string UserRate = "ai-user-rate";
    public const string AccountRate = "ai-account-rate";
    public const string Budget = "ai-budget";

    public string Code { get; } = code;
}

/// <summary>Price arithmetic shared by the ledger and the report, so both agree to the đồng.</summary>
public static class AiPricing
{
    /// <summary>The price whose model matches exactly, else the longest price model the reported model starts with
    /// (providers report dated variants such as "model-2026-10-01").</summary>
    public static AiPriceRecord? Match(IEnumerable<AiPriceRecord> latestPrices, string model)
    {
        var name = model.Trim();
        AiPriceRecord? best = null;
        foreach (var price in latestPrices)
        {
            if (string.Equals(price.Model, name, StringComparison.OrdinalIgnoreCase)) return price;
            if (name.StartsWith(price.Model, StringComparison.OrdinalIgnoreCase) && (best is null || price.Model.Length > best.Model.Length))
                best = price;
        }
        return best;
    }

    public static decimal Cost(AiPriceRecord price, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var total = (inputTokens - cached) * price.InputPerMillion + cached * price.CachedInputPerMillion + outputTokens * price.OutputPerMillion;
        return decimal.Round(total / 1_000_000m, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>The newest price per model that was in effect at <paramref name="at"/>.</summary>
    public static async Task<IReadOnlyList<AiPriceRecord>> LatestAsync(PlatformDbContext database, Guid tenantId, Guid companyId,
        DateTimeOffset at, CancellationToken token)
    {
        var rows = await database.AiPrices.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.CompanyId == companyId && row.EffectiveFromUtc <= at)
            .ToListAsync(token);
        return rows.GroupBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(row => row.EffectiveFromUtc).First()).ToArray();
    }
}
