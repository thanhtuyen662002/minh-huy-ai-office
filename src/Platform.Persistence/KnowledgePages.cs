using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Company notes shown under Kiến thức: general guides and per-customer pages, written by members in the web UI.
public sealed class KnowledgePageRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string? CustomerName { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed record KnowledgePageSummary(Guid Id, string? CustomerName, string Title, Guid UpdatedBy, DateTimeOffset UpdatedAt);
public sealed record KnowledgePageView(Guid Id, string? CustomerName, string Title, string Body, Guid CreatedBy, Guid UpdatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RowVersion);
public sealed record KnowledgePageList(IReadOnlyList<KnowledgePageSummary> Items, bool HasMore);
public sealed record KnowledgePageInput(string Title, string Body, string? CustomerName, string? RowVersion);
public sealed class KnowledgePageNotFoundException() : Exception("Knowledge page is unavailable.");

internal static class KnowledgePageModelConfiguration
{
    internal static void ConfigureKnowledgePages(this ModelBuilder model) => model.Entity<KnowledgePageRecord>(entity =>
    {
        entity.ToTable("KnowledgePages", table => table.HasCheckConstraint("CK_KnowledgePages_Times",
            "[CreatedAt] <= [UpdatedAt] AND DATEPART(tz,[CreatedAt])=0 AND DATEPART(tz,[UpdatedAt])=0"));
        entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Id });
        entity.Property(x => x.CustomerName).HasMaxLength(KnowledgePageJson.MaximumName);
        entity.Property(x => x.Title).HasMaxLength(KnowledgePageJson.MaximumName);
        entity.Property(x => x.Body).HasMaxLength(KnowledgePageJson.MaximumBody);
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.CustomerName, x.Title });
        entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, Id = x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
    });
}

public static class KnowledgePageJson
{
    public const int MaximumName = 200;
    public const int MaximumBody = 20000;
    public const int MaximumBytes = 4 * MaximumBody;
    private static readonly System.Text.UTF8Encoding Utf8 = new(false, true);

    public static KnowledgePageInput Read(ReadOnlyMemory<byte> bytes, bool update)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw Invalid();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("title" or "body" or "customerName" or "rowVersion") || !names.Add(property.Name)) throw Invalid();
        string? Text(string name, bool required)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return required ? throw Invalid() : null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid();
        }
        var title = Text("title", true)!;
        var body = Text("body", true)!;
        var customer = Text("customerName", false);
        var version = Text("rowVersion", update);
        if (!update && version is not null) throw Invalid();
        CustomerRequestJson.Text(title, MaximumName);
        if (customer is not null) CustomerRequestJson.Text(customer, MaximumName);
        // An empty page is allowed; the body only has to be valid text of bounded length.
        if (body.Length > MaximumBody || body.Contains('\0')) throw Invalid();
        try { _ = Utf8.GetByteCount(body); }
        catch (System.Text.EncoderFallbackException) { throw Invalid(); }
        if (version is not null) _ = CustomerRequestJson.Version(version);
        return new(title.Trim(), body, customer?.Trim(), version);
    }

    private static ArgumentException Invalid() => new("Invalid knowledge page.");
}

public sealed class KnowledgeBase(PlatformDbContext database, IAuthorizationDirectory directory)
{
    public const int ListLimit = 500;

    public async Task<KnowledgePageList> ListAsync(AuthorizationContext authority, Guid companyId, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(authority, companyId, cancellationToken);
        var rows = await Scoped(authority).OrderBy(x => x.CustomerName).ThenBy(x => x.Title).ThenBy(x => x.Id).Take(ListLimit + 1)
            .Select(x => new KnowledgePageSummary(x.Id, x.CustomerName, x.Title, x.UpdatedBy, x.UpdatedAt)).ToArrayAsync(cancellationToken);
        await RequireMemberAsync(authority, companyId, cancellationToken);
        return new(rows.Take(ListLimit).ToArray(), rows.Length > ListLimit);
    }

    public async Task<KnowledgePageView> GetAsync(AuthorizationContext authority, Guid companyId, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(authority, companyId, cancellationToken);
        var page = await Scoped(authority).SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new KnowledgePageNotFoundException();
        await RequireMemberAsync(authority, companyId, cancellationToken);
        return View(page);
    }

    public async Task<KnowledgePageView> CreateAsync(AuthorizationContext authority, Guid companyId, KnowledgePageInput input, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(authority, companyId, cancellationToken);
        if (input.RowVersion is not null) throw new ArgumentException("Invalid knowledge page.");
        if (await Scoped(authority).CountAsync(cancellationToken) >= ListLimit) throw new ArgumentException("Knowledge page limit reached.");
        var now = DateTimeOffset.UtcNow;
        var page = new KnowledgePageRecord
        {
            TenantId = authority.TenantId,
            CompanyId = companyId,
            Id = Guid.NewGuid(),
            CustomerName = input.CustomerName,
            Title = input.Title,
            Body = input.Body,
            CreatedBy = authority.UserId,
            UpdatedBy = authority.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (!database.Database.IsSqlServer()) page.RowVersion = RandomNumberGenerator.GetBytes(8);
        database.KnowledgePages.Add(page);
        await RequireMemberAsync(authority, companyId, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return View(page);
    }

    public async Task<KnowledgePageView> UpdateAsync(AuthorizationContext authority, Guid companyId, Guid id, KnowledgePageInput input, CancellationToken cancellationToken = default)
    {
        var page = await LoadForChangeAsync(authority, companyId, id, input.RowVersion, cancellationToken);
        page.Title = input.Title;
        page.Body = input.Body;
        page.CustomerName = input.CustomerName;
        var now = DateTimeOffset.UtcNow;
        page.UpdatedAt = now < page.UpdatedAt ? page.UpdatedAt : now;
        page.UpdatedBy = authority.UserId;
        if (!database.Database.IsSqlServer()) page.RowVersion = RandomNumberGenerator.GetBytes(8);
        await SaveAsync(authority, companyId, cancellationToken);
        return View(page);
    }

    public async Task DeleteAsync(AuthorizationContext authority, Guid companyId, Guid id, string rowVersion, CancellationToken cancellationToken = default)
    {
        var page = await LoadForChangeAsync(authority, companyId, id, rowVersion, cancellationToken);
        database.KnowledgePages.Remove(page);
        await SaveAsync(authority, companyId, cancellationToken);
    }

    private async Task<KnowledgePageRecord> LoadForChangeAsync(AuthorizationContext authority, Guid companyId, Guid id, string? rowVersion,
        CancellationToken cancellationToken)
    {
        await RequireMemberAsync(authority, companyId, cancellationToken);
        var expected = CustomerRequestJson.Version(rowVersion!);
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Knowledge page update is unavailable.");
        var page = await database.KnowledgePages.SingleOrDefaultAsync(x => x.TenantId == authority.TenantId && x.CompanyId == companyId && x.Id == id, cancellationToken)
            ?? throw new KnowledgePageNotFoundException();
        if (!page.RowVersion.SequenceEqual(expected)) throw new CustomerRequestConflictException();
        // Compare against the version the editor saw, not the one just read.
        database.Entry(page).Property(x => x.RowVersion).OriginalValue = expected;
        return page;
    }

    private async Task SaveAsync(AuthorizationContext authority, Guid companyId, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(authority, companyId, cancellationToken);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new CustomerRequestConflictException(); }
    }

    private async Task RequireMemberAsync(AuthorizationContext authority, Guid companyId, CancellationToken cancellationToken)
    {
        if (authority.CompanyId != companyId || (await directory.ResolveAsync(authority, cancellationToken))?.Context != authority)
            throw new UnauthorizedAccessException("Company membership is unavailable.");
    }

    private IQueryable<KnowledgePageRecord> Scoped(AuthorizationContext authority) => database.KnowledgePages.AsNoTracking()
        .Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId);

    private static KnowledgePageView View(KnowledgePageRecord page) => new(page.Id, page.CustomerName, page.Title, page.Body,
        page.CreatedBy, page.UpdatedBy, page.CreatedAt, page.UpdatedAt, Convert.ToBase64String(page.RowVersion));
}
