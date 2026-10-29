using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record CustomerRequestDigestItem(string Code, string CustomerName, string SourceGroupName, string Summary,
    string Status, string? AssigneeName, DateTimeOffset CreatedAt, int AgeDays);
public sealed record CustomerRequestClosedItem(string Code, string CustomerName, string SourceGroupName, string Summary,
    string Status, string? AssigneeName, DateTimeOffset ClosedAt);
public sealed record CustomerRequestDigest(DateTimeOffset GeneratedAt, DateTimeOffset Since, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<CustomerRequestDigestItem> Open, int OpenTotal, IReadOnlyList<CustomerRequestClosedItem> ClosedSince, int ClosedTotal);

// Read-only status digest for the bot that posts into the internal technical group.
// No evidence quotes or message content beyond the ticket summary leave this boundary.
public sealed class CustomerRequestDigestReader(PlatformDbContext database, CustomerRequestAuthenticator authentication, TimeProvider clock)
{
    public const string Path = CustomerRequestAuthenticator.Path + "/digest";
    public const int MaximumItems = 50;
    public const int SummaryLength = 120;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(31);
    private static readonly string[] Statuses = ["new", "needs_clarification", "in_progress", "done", "rejected"];

    public static string Target(long? since) => since is null ? Path : Path + "?since=" + since.Value.ToString(CultureInfo.InvariantCulture);

    public async Task<CustomerRequestDigest> ReadAsync(CustomerRequestSignature signature, long? since, CancellationToken ct = default)
    {
        var binding = await authentication.AuthenticateAsync(signature, ReadOnlyMemory<byte>.Empty, "GET", Target(since), null, ct);
        var now = clock.GetUtcNow();
        DateTimeOffset from;
        try { from = since is null ? now - DefaultWindow : DateTimeOffset.FromUnixTimeSeconds(since.Value); }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("Invalid digest window."); }
        if (from > now || now - from > MaximumWindow) throw new ArgumentException("Invalid digest window.");
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Customer request digest is unavailable.");
        // Nonces are consumed in a write transaction even though the digest itself only reads.
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, ct);
        await CustomerRequestLocks.AcquireAsync(database, $"service:{signature.ServiceId:D}", ct);
        if (!await database.Companies.AsNoTracking().AnyAsync(x => x.TenantId == binding.TenantId && x.Id == binding.CompanyId && x.IsActive, ct))
            throw CustomerRequestServiceDirectory.Denied();
        authentication.RequireFresh(signature.Timestamp);
        await CustomerRequestReplayProtection.ConsumeAsync(database, signature, clock, ct);

        var groups = binding.AllowedSourceGroupExternalIds.ToArray();
        var scoped = database.CustomerRequests.AsNoTracking().Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId &&
            groups.Contains(x.SourceGroupExternalId));
        var grouped = await scoped.GroupBy(x => x.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToArrayAsync(ct);
        var counts = Statuses.ToDictionary(x => x, x => grouped.Where(g => g.Status == x).Sum(g => g.Count), StringComparer.Ordinal);
        var openRows = await scoped.Where(x => x.Status != "done" && x.Status != "rejected")
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Code).Take(MaximumItems).ToArrayAsync(ct);
        var closedEvents = await database.CustomerRequestEvents.AsNoTracking()
            .Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId && x.EventType == "status_changed" &&
                (x.NewValue == "done" || x.NewValue == "rejected") && x.Timestamp >= from)
            .Select(x => new { x.RequestId, x.Timestamp }).ToArrayAsync(ct);
        var closedAt = closedEvents.GroupBy(x => x.RequestId).ToDictionary(x => x.Key, x => x.Max(e => e.Timestamp));
        var closedIds = closedAt.Keys.ToArray();
        var closedRows = closedIds.Length == 0 ? Array.Empty<CustomerRequestRecord>() : await scoped
            .Where(x => closedIds.Contains(x.Id) && (x.Status == "done" || x.Status == "rejected")).ToArrayAsync(ct);
        closedRows = closedRows.OrderByDescending(x => closedAt[x.Id]).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();

        var assigneeIds = openRows.Concat(closedRows.Take(MaximumItems)).Select(x => x.AssigneeUserId).OfType<Guid>().Distinct().ToArray();
        var names = assigneeIds.Length == 0 ? new Dictionary<Guid, string>() : await database.Users.AsNoTracking()
            .Where(x => x.TenantId == binding.TenantId && assigneeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        authentication.RequireFresh(signature.Timestamp);
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        string? Name(Guid? id) => id is Guid value && names.TryGetValue(value, out var name) ? name : null;
        var open = openRows.Select(x => new CustomerRequestDigestItem(x.Code, x.CustomerName, x.SourceGroupName, Shorten(x.Summary), x.Status,
            Name(x.AssigneeUserId), x.CreatedAt, Math.Max(0, (int)(now - x.CreatedAt).TotalDays))).ToArray();
        var closed = closedRows.Take(MaximumItems).Select(x => new CustomerRequestClosedItem(x.Code, x.CustomerName, x.SourceGroupName,
            Shorten(x.Summary), x.Status, Name(x.AssigneeUserId), closedAt[x.Id])).ToArray();
        return new(now, from, counts, open, counts["new"] + counts["needs_clarification"] + counts["in_progress"], closed, closedRows.Length);
    }

    private static string Shorten(string value)
    {
        if (value.Length <= SummaryLength) return value;
        var cut = SummaryLength - 1;
        if (char.IsHighSurrogate(value[cut - 1])) cut--;
        return value[..cut] + "…";
    }
}
