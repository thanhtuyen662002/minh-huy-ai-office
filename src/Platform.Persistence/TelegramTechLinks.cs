using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// A company member's Telegram account, linked through a one-time code the member created while signed in.
// Only the bot service may present the code; the Telegram user id alone never grants anything.
public sealed class TelegramAccountLinkRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public long TelegramUserId { get; set; }
    public required string TelegramName { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class TelegramLinkCodeRecord
{
    public byte[] CodeHash { get; set; } = [];
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed record TelegramOptions(string? BotUsername)
{
    private static readonly Regex Username = new("^[A-Za-z][A-Za-z0-9_]{3,30}[Bb][Oo][Tt]$", RegexOptions.CultureInvariant);
    public static TelegramOptions Create(string? botUsername) =>
        string.IsNullOrWhiteSpace(botUsername) ? new((string?)null) :
        Username.IsMatch(botUsername) ? new(botUsername) : throw new InvalidOperationException("Invalid Telegram bot username.");
}

public sealed record TelegramLinkView(bool Linked, string? TelegramName, DateTimeOffset? LinkedAt, string? BotUsername);
public sealed record TelegramLinkCode(string Code, string? Url, DateTimeOffset ExpiresAt);

public sealed class TelegramLinkPortal(PlatformDbContext database, IAuthorizationDirectory directory, TelegramOptions options, TimeProvider clock)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    public async Task<TelegramLinkView> GetAsync(AuthorizationContext authority, Guid companyId, CancellationToken ct = default)
    {
        await RequireMemberAsync(authority, companyId, ct);
        var link = await Links(authority).SingleOrDefaultAsync(ct);
        return new(link is not null, link?.TelegramName, link?.LinkedAt, options.BotUsername);
    }

    public async Task<TelegramLinkCode> CreateCodeAsync(AuthorizationContext authority, Guid companyId, CancellationToken ct = default)
    {
        await RequireMemberAsync(authority, companyId, ct);
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Telegram link is unavailable.");
        var now = clock.GetUtcNow();
        var code = TelegramTechJson.NewCode();
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, ct);
        // A member holds at most one live code; expired codes of anyone are swept on the way.
        database.TelegramLinkCodes.RemoveRange(await database.TelegramLinkCodes.Where(x => x.ExpiresAt < now ||
            x.TenantId == authority.TenantId && x.CompanyId == companyId && x.UserId == authority.UserId).ToArrayAsync(ct));
        var expires = now + CodeLifetime;
        database.TelegramLinkCodes.Add(new()
        {
            CodeHash = TelegramTechJson.Hash(code),
            TenantId = authority.TenantId,
            CompanyId = companyId,
            UserId = authority.UserId,
            ExpiresAt = expires
        });
        await RequireMemberAsync(authority, companyId, ct);
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(code, options.BotUsername is null ? null : $"https://t.me/{options.BotUsername}?start={code}", expires);
    }

    public async Task UnlinkAsync(AuthorizationContext authority, Guid companyId, CancellationToken ct = default)
    {
        await RequireMemberAsync(authority, companyId, ct);
        database.TelegramAccountLinks.RemoveRange(await Links(authority).AsTracking().ToArrayAsync(ct));
        await database.SaveChangesAsync(ct);
    }

    private IQueryable<TelegramAccountLinkRecord> Links(AuthorizationContext authority) => database.TelegramAccountLinks.AsNoTracking()
        .Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId && x.UserId == authority.UserId);

    private async Task RequireMemberAsync(AuthorizationContext authority, Guid companyId, CancellationToken ct)
    {
        if (authority.CompanyId != companyId || (await directory.ResolveAsync(authority, ct))?.Context != authority)
            throw new UnauthorizedAccessException("Company membership is unavailable.");
    }
}

public sealed record TelegramLinkInput(string Code, long TelegramUserId, string TelegramName);
public sealed record TelegramActionInput(string Code, string Action, long TelegramUserId, string TelegramName, string SourceMessageId);
public sealed record TelegramTicketsInput(long TelegramUserId, string Scope);
public sealed record TelegramTicketInput(long TelegramUserId, string Code);

public sealed record TelegramLinkReceipt(string DisplayName);
// Outcome: ok | unchanged | taken (someone else holds the ticket) | not_linked
public sealed record TelegramActionReceipt(string Outcome, string? Code, string? Status, string? AssigneeName);
public sealed record TelegramTicketItem(string Code, string CustomerName, string SourceGroupName, string Summary, string Status,
    string? AssigneeName, DateTimeOffset CreatedAt, int AgeDays);
public sealed record TelegramTicketList(bool Linked, string? DisplayName, IReadOnlyList<TelegramTicketItem> Items, int Total);
public sealed record TelegramTicketDetail(string Code, string Status, string CustomerName, string SourceGroupName, string ReportedBy,
    string Summary, string? CustomerDesiredDueText, string? AssigneeName, DateTimeOffset CreatedAt,
    IReadOnlyList<CustomerRequestItemInput> Items, IReadOnlyList<string> OpenQuestions);
public sealed record TelegramTicketView(bool Linked, TelegramTicketDetail? Ticket);

public static class TelegramTechJson
{
    public const int MaximumBytes = 2048;
    public static readonly string[] Actions = ["claim", "in_progress", "needs_clarification", "done", "release"];
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 4
    };

    public static TelegramLinkInput ReadLink(ReadOnlyMemory<byte> bytes)
    {
        var input = Read<TelegramLinkInput>(bytes);
        if (!IsCode(input.Code)) throw Invalid();
        User(input.TelegramUserId, input.TelegramName);
        return input;
    }

    public static TelegramActionInput ReadAction(ReadOnlyMemory<byte> bytes)
    {
        var input = Read<TelegramActionInput>(bytes);
        CustomerRequestJson.Text(input.Code, 24, opaque: true);
        if (!Actions.Contains(input.Action)) throw Invalid();
        User(input.TelegramUserId, input.TelegramName);
        CustomerRequestJson.Text(input.SourceMessageId, 190, opaque: true);
        return input;
    }

    public static TelegramTicketsInput ReadTickets(ReadOnlyMemory<byte> bytes)
    {
        var input = Read<TelegramTicketsInput>(bytes);
        if (input.TelegramUserId <= 0 || input.Scope is not ("mine" or "open")) throw Invalid();
        return input;
    }

    public static TelegramTicketInput ReadTicket(ReadOnlyMemory<byte> bytes)
    {
        var input = Read<TelegramTicketInput>(bytes);
        if (input.TelegramUserId <= 0) throw Invalid();
        CustomerRequestJson.Text(input.Code, 24, opaque: true);
        return input;
    }

    // 16 random bytes as unpadded base64url: 22 characters, valid as a Telegram /start payload.
    public static string NewCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static bool IsCode(string? value) => value is { Length: 22 } && value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
    public static byte[] Hash(string code) => SHA256.HashData(Encoding.ASCII.GetBytes(code));

    private static void User(long id, string name)
    {
        if (id <= 0) throw Invalid();
        CustomerRequestJson.Text(name, 200);
    }

    private static T Read<T>(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw Invalid();
        _ = new UTF8Encoding(false, true).GetCharCount(bytes.Span);
        return JsonSerializer.Deserialize<T>(bytes.Span, Options) ?? throw Invalid();
    }

    private static ArgumentException Invalid() => new("Invalid Telegram request.");
}

// Signed calls from the bot on behalf of a linked technician. The service scope (company and source groups)
// bounds every read and write; the linked member must still be active in that company.
public sealed class TelegramTechBridge(PlatformDbContext database, CustomerRequestAuthenticator authentication,
    IAuthorizationDirectory directory, TimeProvider clock)
{
    public const string Root = CustomerRequestAuthenticator.Path + "/telegram";
    public const string LinkPath = Root + "/link";
    public const string ActionPath = Root + "/action";
    public const string TicketsPath = Root + "/tickets";
    public const string TicketPath = Root + "/ticket";
    public const int MaximumItems = 20;
    public const int SummaryLength = 200;
    private const string ActorType = "user";

    public async Task<TelegramLinkReceipt> LinkAsync(CustomerRequestSignature signature, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
        await Run(signature, body, LinkPath, TelegramTechJson.ReadLink, async (binding, _, input) =>
        {
            var now = clock.GetUtcNow();
            var hash = TelegramTechJson.Hash(input.Code);
            var code = await database.TelegramLinkCodes.SingleOrDefaultAsync(x => x.CodeHash == hash, ct);
            if (code is null || code.TenantId != binding.TenantId || code.CompanyId != binding.CompanyId) throw new CustomerRequestNotFoundException();
            if (code.ExpiresAt < now) throw new CustomerRequestNotFoundException();
            database.TelegramLinkCodes.Remove(code);
            var member = await Member(binding, code.UserId, ct) ?? throw new CustomerRequestNotFoundException();
            // One Telegram account per member and one member per Telegram account.
            database.TelegramAccountLinks.RemoveRange(await database.TelegramAccountLinks.Where(x => x.TenantId == binding.TenantId &&
                x.CompanyId == binding.CompanyId && (x.UserId == code.UserId || x.TelegramUserId == input.TelegramUserId)).ToArrayAsync(ct));
            await database.SaveChangesAsync(ct);
            database.TelegramAccountLinks.Add(new()
            {
                TenantId = binding.TenantId,
                CompanyId = binding.CompanyId,
                UserId = code.UserId,
                TelegramUserId = input.TelegramUserId,
                TelegramName = input.TelegramName,
                LinkedAt = now
            });
            return new TelegramLinkReceipt(member.DisplayName);
        }, ct);

    public async Task<TelegramActionReceipt> ActAsync(CustomerRequestSignature signature, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
        await Run(signature, body, ActionPath, TelegramTechJson.ReadAction, async (binding, groups, input) =>
        {
            var actor = await Actor(binding, input.TelegramUserId, ct);
            if (actor is null) return new TelegramActionReceipt("not_linked", null, null, null);
            var ticket = await database.CustomerRequests.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == binding.TenantId &&
                x.CompanyId == binding.CompanyId && x.Code == input.Code && groups.Contains(x.SourceGroupExternalId), ct)
                ?? throw new CustomerRequestNotFoundException();
            if (await database.CustomerRequestEvents.AsNoTracking().AnyAsync(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId &&
                x.RequestId == ticket.Id && (x.SourceMessageId == input.SourceMessageId || x.SourceMessageId == input.SourceMessageId + ":s"), ct))
                return await Receipt("unchanged", ticket, binding, ct);
            var mine = ticket.AssigneeUserId == actor.UserId;
            if (ticket.AssigneeUserId is not null && !mine) return await Receipt("taken", ticket, binding, ct);
            Guid? assignee = ticket.AssigneeUserId;
            var status = ticket.Status;
            switch (input.Action)
            {
                case "claim":
                    if (ticket.Status is "done" or "rejected") return await Receipt("unchanged", ticket, binding, ct);
                    assignee = actor.UserId;
                    if (status == "new") status = "in_progress";
                    break;
                case "release":
                    if (!mine) return await Receipt("unchanged", ticket, binding, ct);
                    assignee = null;
                    if (status == "in_progress") status = "new";
                    break;
                default:
                    assignee = actor.UserId;
                    status = input.Action;
                    break;
            }
            if (assignee == ticket.AssigneeUserId && status == ticket.Status) return await Receipt("unchanged", ticket, binding, ct);
            var now = clock.GetUtcNow();
            database.Attach(ticket);
            var source = input.SourceMessageId;
            if (assignee != ticket.AssigneeUserId)
            {
                Event("assignee_changed", ticket.AssigneeUserId?.ToString("D"), assignee?.ToString("D"), source);
                ticket.AssigneeUserId = assignee;
                source += ":s";
            }
            if (status != ticket.Status)
            {
                Event("status_changed", ticket.Status, status, source);
                ticket.Status = status;
            }
            ticket.UpdatedAt = now < ticket.UpdatedAt ? ticket.UpdatedAt : now;
            if (!database.Database.IsSqlServer()) ticket.RowVersion = RandomNumberGenerator.GetBytes(8);
            return new TelegramActionReceipt("ok", ticket.Code, ticket.Status, ticket.AssigneeUserId is null ? null : actor.DisplayName);

            void Event(string type, string? before, string? after, string message) => database.CustomerRequestEvents.Add(new()
            {
                TenantId = binding.TenantId,
                CompanyId = binding.CompanyId,
                RequestId = ticket.Id,
                Id = Guid.NewGuid(),
                EventType = type,
                ActorType = ActorType,
                ActorId = actor.UserId,
                ActorName = actor.DisplayName,
                ActorExternalId = "telegram:" + input.TelegramUserId,
                SourceMessageId = message,
                OldValue = before,
                NewValue = after,
                Timestamp = now
            });
        }, ct);

    public async Task<TelegramTicketList> ListAsync(CustomerRequestSignature signature, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
        await Run(signature, body, TicketsPath, TelegramTechJson.ReadTickets, async (binding, groups, input) =>
        {
            var actor = await Actor(binding, input.TelegramUserId, ct);
            if (actor is null) return new TelegramTicketList(false, null, [], 0);
            var scoped = database.CustomerRequests.AsNoTracking().Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId &&
                groups.Contains(x.SourceGroupExternalId) && x.Status != "done" && x.Status != "rejected");
            scoped = input.Scope == "mine" ? scoped.Where(x => x.AssigneeUserId == actor.UserId) : scoped.Where(x => x.AssigneeUserId == null);
            var total = await scoped.CountAsync(ct);
            var rows = await scoped.OrderBy(x => x.CreatedAt).ThenBy(x => x.Code).Take(MaximumItems).ToArrayAsync(ct);
            var now = clock.GetUtcNow();
            var items = rows.Select(x => new TelegramTicketItem(x.Code, x.CustomerName, x.SourceGroupName, Shorten(x.Summary), x.Status,
                x.AssigneeUserId is null ? null : actor.DisplayName, x.CreatedAt, Math.Max(0, (int)(now - x.CreatedAt).TotalDays))).ToArray();
            return new TelegramTicketList(true, actor.DisplayName, items, total);
        }, ct);

    public async Task<TelegramTicketView> GetAsync(CustomerRequestSignature signature, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
        await Run(signature, body, TicketPath, TelegramTechJson.ReadTicket, async (binding, groups, input) =>
        {
            if (await Actor(binding, input.TelegramUserId, ct) is null) return new TelegramTicketView(false, null);
            var ticket = await database.CustomerRequests.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == binding.TenantId &&
                x.CompanyId == binding.CompanyId && x.Code == input.Code && groups.Contains(x.SourceGroupExternalId), ct)
                ?? throw new CustomerRequestNotFoundException();
            var items = await database.CustomerRequestItems.AsNoTracking().Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId &&
                x.RequestId == ticket.Id).OrderBy(x => x.Ordinal).Select(x => new CustomerRequestItemInput(x.Content, x.Priority, x.Kind)).ToArrayAsync(ct);
            var questions = await database.CustomerRequestOpenQuestions.AsNoTracking().Where(x => x.TenantId == binding.TenantId &&
                x.CompanyId == binding.CompanyId && x.RequestId == ticket.Id).OrderBy(x => x.Ordinal).Select(x => x.Content).ToArrayAsync(ct);
            return new TelegramTicketView(true, new(ticket.Code, ticket.Status, ticket.CustomerName, ticket.SourceGroupName, ticket.ReportedBy,
                ticket.Summary, ticket.CustomerDesiredDueText, await Name(binding, ticket.AssigneeUserId, ct), ticket.CreatedAt, items, questions));
        }, ct);

    // Authenticate, lock, consume the nonce and resolve the service's group scope, then run one operation in that transaction.
    private async Task<TResult> Run<TInput, TResult>(CustomerRequestSignature signature, ReadOnlyMemory<byte> body, string path,
        Func<ReadOnlyMemory<byte>, TInput> read, Func<CustomerRequestServiceBinding, string[], TInput, Task<TResult>> operation, CancellationToken ct)
    {
        if (body.Length is < 1 or > TelegramTechJson.MaximumBytes) throw new ArgumentException("Invalid Telegram request.");
        var captured = body.ToArray();
        try
        {
            var input = read(captured);
            var binding = await authentication.AuthenticateAsync(signature, captured, "POST", path, null, ct);
            if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Telegram request is unavailable.");
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, ct);
            await CustomerRequestLocks.AcquireAsync(database, $"service:{signature.ServiceId:D}", ct);
            await CustomerRequestLocks.AcquireAsync(database, $"company:{binding.TenantId:D}:{binding.CompanyId:D}", ct);
            if (!await database.Companies.AsNoTracking().AnyAsync(x => x.TenantId == binding.TenantId && x.Id == binding.CompanyId && x.IsActive, ct))
                throw CustomerRequestServiceDirectory.Denied();
            authentication.RequireFresh(signature.Timestamp);
            await CustomerRequestReplayProtection.ConsumeAsync(database, signature, clock, ct);
            var groups = await CustomerRequestGroupGrants.GroupsAsync(database, binding, ct);
            var result = await operation(binding, groups, input);
            authentication.RequireFresh(signature.Timestamp);
            try { await database.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new CustomerRequestConflictException(); }
            await transaction.CommitAsync(ct);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(captured); }
    }

    private sealed record LinkedActor(Guid UserId, string DisplayName);

    private async Task<LinkedActor?> Actor(CustomerRequestServiceBinding binding, long telegramUserId, CancellationToken ct)
    {
        var link = await database.TelegramAccountLinks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == binding.TenantId &&
            x.CompanyId == binding.CompanyId && x.TelegramUserId == telegramUserId, ct);
        if (link is null) return null;
        var member = await Member(binding, link.UserId, ct);
        return member is null ? null : new(link.UserId, member.DisplayName);
    }

    private async Task<PlatformUserRecord?> Member(CustomerRequestServiceBinding binding, Guid userId, CancellationToken ct)
    {
        var context = AuthorizationContext.Create(binding.TenantId, binding.CompanyId, userId);
        if ((await directory.ResolveAsync(context, ct))?.Context != context) return null;
        return await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == binding.TenantId && x.Id == userId, ct);
    }

    private async Task<string?> Name(CustomerRequestServiceBinding binding, Guid? userId, CancellationToken ct) => userId is null ? null :
        await database.Users.AsNoTracking().Where(x => x.TenantId == binding.TenantId && x.Id == userId).Select(x => x.DisplayName).SingleOrDefaultAsync(ct);

    private async Task<TelegramActionReceipt> Receipt(string outcome, CustomerRequestRecord ticket, CustomerRequestServiceBinding binding, CancellationToken ct) =>
        new(outcome, ticket.Code, ticket.Status, await Name(binding, ticket.AssigneeUserId, ct));

    private static string Shorten(string value)
    {
        if (value.Length <= SummaryLength) return value;
        var cut = SummaryLength - 1;
        if (char.IsHighSurrogate(value[cut - 1])) cut--;
        return value[..cut] + "…";
    }
}

internal static class TelegramTechModelConfiguration
{
    internal static void ConfigureTelegramTech(this ModelBuilder model)
    {
        model.Entity<TelegramAccountLinkRecord>(entity =>
        {
            entity.ToTable("TelegramAccountLinks", table => table.HasCheckConstraint("CK_TelegramAccountLinks_Values", "[TelegramUserId] > 0 AND DATEPART(tz,[LinkedAt])=0"));
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.UserId });
            entity.Property(x => x.TelegramName).HasMaxLength(200);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.TelegramUserId }).IsUnique();
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<TelegramLinkCodeRecord>(entity =>
        {
            entity.ToTable("TelegramLinkCodes", table => table.HasCheckConstraint("CK_TelegramLinkCodes_Hash", "DATALENGTH([CodeHash]) = 32"));
            entity.HasKey(x => x.CodeHash);
            entity.Property(x => x.CodeHash).HasMaxLength(32).IsFixedLength();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.UserId });
            entity.HasIndex(x => x.ExpiresAt);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
