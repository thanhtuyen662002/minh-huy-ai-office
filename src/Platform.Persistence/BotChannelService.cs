using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class BotChannelService(PlatformDbContext database, IAuthorizationDirectory directory,
    BotSecretProtector protector, TimeProvider clock)
{
    public async Task<BotAdminView> GetAsync(AuthorizationContext authority, CancellationToken ct = default)
    {
        await RequireAdminAsync(authority, ct);
        var channels = new List<BotChannelView>();
        foreach (var channel in BotChannelJson.Channels) channels.Add(await ChannelViewAsync(authority.TenantId, authority.CompanyId, channel, ct));
        var ai = await AiAsync(authority.TenantId, authority.CompanyId, ct);
        await RequireAdminAsync(authority, ct);
        return new(true, channels.ToArray(), new(ai?.BaseUrl ?? "https://api.openai.com/v1", ai?.Model ?? "", ai?.ProtectedApiKey is not null, Version(ai?.Version ?? 0)));
    }

    public async Task<BotChannelView> UpdateAsync(AuthorizationContext authority, string channel, BotChannelUpdate input, CancellationToken ct = default)
    {
        await RequireAdminAsync(authority, ct); BotChannelJson.Channel(channel);
        var expected = BotChannelJson.Version(input.ExpectedVersion); var schedule = BotChannelJson.Schedule(input.SummarySchedule);
        if (input.CustomerGroups is null || input.CustomerGroups.Length > 100) throw new ArgumentException();
        await using var tx = await BeginCompanyAsync(authority.TenantId, authority.CompanyId, ct);
        await RequireAdminAsync(authority, ct);
        var row = await ChannelAsync(authority.TenantId, authority.CompanyId, channel, ct);
        if ((row?.ConfigVersion ?? 0) != expected) throw new BotChannelConflictException();
        var status = await StatusAsync(authority.TenantId, authority.CompanyId, channel, ct);
        var reported = status is null ? [] : Deserialize<BotGroup[]>(status.GroupsJson);
        if (input.TechGroupExternalId is not null && !reported.Any(x => x.ExternalId == input.TechGroupExternalId)) throw new ArgumentException();
        var selected = input.CustomerGroups.Select(group =>
        {
            if (group is null) throw new ArgumentException();
            CustomerRequestJson.Text(group.ExternalId, 200, opaque: true); CustomerRequestJson.Text(group.CustomerName, 200, opaque: true);
            var known = reported.SingleOrDefault(x => x.ExternalId == group.ExternalId) ?? throw new ArgumentException();
            if (group.DisplayName != known.DisplayName || group.ExternalId == input.TechGroupExternalId) throw new ArgumentException();
            return new BotCustomerGroup(known.ExternalId, known.DisplayName, group.CustomerName);
        }).OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToArray();
        if (selected.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() != selected.Length ||
            input.Enabled && (input.TechGroupExternalId is null || selected.Length == 0)) throw new ArgumentException();
        var oldGroups = await GroupsAsync(authority.TenantId, authority.CompanyId, channel, ct);
        var before = row is null ? new BotChannelRecord { TenantId = authority.TenantId, CompanyId = authority.CompanyId, Channel = channel } : row;
        var changes = new List<(string Field, string? Before, string? After)>();
        Change("enabled", before.Enabled.ToString(), input.Enabled.ToString()); Change("dryRun", before.DryRun.ToString(), input.DryRun.ToString());
        Change("summarySchedule", before.SummaryScheduleJson, JsonSerializer.Serialize(schedule)); Change("techGroupExternalId", before.TechGroupExternalId, input.TechGroupExternalId);
        foreach (var id in oldGroups.Select(x => x.ExternalId).Union(selected.Select(x => x.ExternalId), StringComparer.Ordinal))
        {
            var old = oldGroups.SingleOrDefault(x => x.ExternalId == id); var next = selected.SingleOrDefault(x => x.ExternalId == id);
            if (old != next) changes.Add(("customerGroups", old is null ? null : JsonSerializer.Serialize(old), next is null ? null : JsonSerializer.Serialize(next)));
        }
        if (changes.Count > 0)
        {
            if (row is null) { row = before; database.BotChannels.Add(row); row.CreatedAt = clock.GetUtcNow(); }
            else { Attach(row); row.ConfigVersion = checked(row.ConfigVersion + 1); }
            row.Enabled = input.Enabled; row.DryRun = input.DryRun; row.SummaryScheduleJson = JsonSerializer.Serialize(schedule); row.TechGroupExternalId = input.TechGroupExternalId;
            row.UpdatedAt = clock.GetUtcNow(); Stamp(row);
            var existing = await database.BotCustomerGroups.Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId && x.Channel == channel).ToArrayAsync(ct);
            foreach (var group in existing)
            {
                var next = selected.SingleOrDefault(x => x.ExternalId == group.ExternalId);
                if (next is null) database.BotCustomerGroups.Remove(group);
                else { group.DisplayName = next.DisplayName; group.CustomerName = next.CustomerName; }
            }
            foreach (var group in selected.Where(x => !existing.Any(old => old.ExternalId == x.ExternalId)))
                database.BotCustomerGroups.Add(new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, Channel = channel, ExternalId = group.ExternalId, DisplayName = group.DisplayName, CustomerName = group.CustomerName });
            foreach (var change in changes) Audit(authority, channel, change.Field, change.Before, change.After);
            await RequireAdminAsync(authority, ct); await database.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await ChannelViewAsync(authority.TenantId, authority.CompanyId, channel, ct);
        void Change(string field, string? old, string? next) { if (old != next) changes.Add((field, old, next)); }
    }

    public async Task<BotAiView> UpdateAiAsync(AuthorizationContext authority, BotAiUpdate input, CancellationToken ct = default)
    {
        await RequireAdminAsync(authority, ct);
        var expected = BotChannelJson.Version(input.ExpectedVersion); var url = BotChannelJson.BaseUrl(input.BaseUrl);
        CustomerRequestJson.Text(input.Model, 200, opaque: true);
        if (input.ClearApiKey && input.ApiKey is not null) throw new ArgumentException();
        if (input.ApiKey is not null) CustomerRequestJson.Text(input.ApiKey, 4096, opaque: true);
        await using var tx = await BeginCompanyAsync(authority.TenantId, authority.CompanyId, ct); await RequireAdminAsync(authority, ct);
        var row = await AiAsync(authority.TenantId, authority.CompanyId, ct);
        if ((row?.Version ?? 0) != expected) throw new BotChannelConflictException();
        var keyChanged = input.ClearApiKey ? row?.ProtectedApiKey is not null : input.ApiKey is not null &&
            (row?.ProtectedApiKey is null || input.ApiKey != await protector.UnprotectAsync(authority.TenantId, authority.CompanyId, row.ProtectedApiKey, ct));
        if (row is null || row.BaseUrl != url || row.Model != input.Model || keyChanged)
        {
            var oldUrl = row?.BaseUrl; var oldModel = row?.Model;
            if (row is null) { row = new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId }; database.BotAiSettings.Add(row); }
            else { Attach(row); row.Version = checked(row.Version + 1); }
            if (keyChanged) row.ProtectedApiKey = input.ClearApiKey ? null : await protector.ProtectAsync(authority.TenantId, authority.CompanyId, input.ApiKey!, ct);
            row.BaseUrl = url; row.Model = input.Model; row.UpdatedAt = clock.GetUtcNow(); Stamp(row);
            if (oldUrl != url) Audit(authority, null, "ai.baseUrl", oldUrl, url);
            if (oldModel != input.Model) Audit(authority, null, "ai.model", oldModel, input.Model);
            if (keyChanged) Audit(authority, null, "ai.apiKey", "[redacted]", input.ClearApiKey ? "cleared" : "[redacted]");
            foreach (var channel in BotChannelJson.Channels)
            {
                var config = await EnsureChannelAsync(authority.TenantId, authority.CompanyId, channel, ct);
                if (database.Entry(config).State != EntityState.Added) { Attach(config); config.ConfigVersion = checked(config.ConfigVersion + 1); }
                config.UpdatedAt = clock.GetUtcNow(); Stamp(config);
            }
            await RequireAdminAsync(authority, ct); await database.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(row.BaseUrl, row.Model, row.ProtectedApiKey is not null, Version(row.Version));
    }

    public async Task<BotCommandView> EnqueueAsync(AuthorizationContext authority, string channel, string kind, CancellationToken ct = default)
    {
        await RequireAdminAsync(authority, ct); BotChannelJson.Channel(channel);
        if (kind is not ("summarize-now" or "relogin")) throw new ArgumentException();
        await using var tx = await BeginCompanyAsync(authority.TenantId, authority.CompanyId, ct); await RequireAdminAsync(authority, ct);
        await EnsureChannelAsync(authority.TenantId, authority.CompanyId, channel, ct);
        var now = clock.GetUtcNow();
        var existing = await database.BotCommands.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId &&
            x.Channel == channel && x.Kind == kind && x.AcknowledgedAt == null && x.ExpiresAt > now, ct);
        if (existing is not null) { await tx.CommitAsync(ct); return Command(existing); }
        var command = new BotCommandRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            Channel = channel,
            Id = Guid.NewGuid(),
            Kind = kind,
            CreatedByUserId = authority.UserId,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        database.BotCommands.Add(command); Audit(authority, channel, "command", null, kind);
        if (kind == "relogin")
        {
            var status = await StatusAsync(authority.TenantId, authority.CompanyId, channel, ct);
            if (status is not null) { Attach(status); ClearQr(status); Stamp(status); }
        }
        await RequireAdminAsync(authority, ct); await database.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Command(command);
    }

    public async Task<BotLoginQr?> QrAsync(AuthorizationContext authority, string channel, CancellationToken ct = default)
    {
        await RequireAdminAsync(authority, ct); BotChannelJson.Channel(channel);
        var status = await StatusAsync(authority.TenantId, authority.CompanyId, channel, ct);
        await RequireAdminAsync(authority, ct);
        return status?.LoginState == "awaiting_qr" && status.LoginQrImage is not null && status.LoginQrExpiresAt > clock.GetUtcNow()
            ? new(status.LoginQrContentType!, Convert.ToBase64String(status.LoginQrImage), status.LoginQrExpiresAt.Value) : null;
    }

    public async Task<BotAuditView[]> AuditAsync(AuthorizationContext authority, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        await RequireAdminAsync(authority, ct);
        if (offset is < 0 or > 100000 || limit is < 1 or > 100) throw new ArgumentException();
        var rows = await database.BotChannelAudits.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId)
            .OrderByDescending(x => x.Timestamp).ThenBy(x => x.Id).Skip(offset).Take(limit)
            .Select(x => new BotAuditView(x.Id, x.Channel, x.ActorUserId, x.Field, x.Before, x.After, x.Timestamp)).ToArrayAsync(ct);
        await RequireAdminAsync(authority, ct); return rows;
    }

    public async Task<BotAiConfig> ProbeSettingsAsync(AuthorizationContext authority, CancellationToken ct = default)
    {
        await RequireAdminAsync(authority, ct); var ai = await AiAsync(authority.TenantId, authority.CompanyId, ct);
        if (ai is null) throw new BotChannelNotFoundException();
        var key = ai.ProtectedApiKey is null ? null : await protector.UnprotectAsync(authority.TenantId, authority.CompanyId, ai.ProtectedApiKey, ct);
        await RequireAdminAsync(authority, ct); return new(ai.BaseUrl, ai.Model, key);
    }

    internal async Task<BotConfigView> PullAsync(CustomerRequestServiceBinding binding, string channel, CancellationToken ct)
    {
        var config = await ChannelViewAsync(binding.TenantId, binding.CompanyId, channel, ct);
        var ai = await AiAsync(binding.TenantId, binding.CompanyId, ct); var now = clock.GetUtcNow();
        var commands = await database.BotCommands.AsNoTracking().Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId &&
            x.Channel == channel && x.AcknowledgedAt == null && x.ExpiresAt > now).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(10).ToArrayAsync(ct);
        var key = ai?.ProtectedApiKey is null ? null : await protector.UnprotectAsync(binding.TenantId, binding.CompanyId, ai.ProtectedApiKey, ct);
        // Reported group/status data is never needed in the bot configuration response.
        return new(config with { Status = null }, new(ai?.BaseUrl ?? "https://api.openai.com/v1", ai?.Model ?? "", key), commands.Select(Command).ToArray());
    }

    internal async Task HeartbeatAsync(CustomerRequestServiceBinding binding, BotHeartbeat input, CancellationToken ct)
    {
        input = BotChannelJson.Heartbeat(input, clock.GetUtcNow());
        await EnsureChannelAsync(binding.TenantId, binding.CompanyId, input.Channel, ct);
        var row = await StatusAsync(binding.TenantId, binding.CompanyId, input.Channel, ct);
        var fresh = row is null;
        if (row is null) { row = new() { TenantId = binding.TenantId, CompanyId = binding.CompanyId, Channel = input.Channel, LoginState = input.LoginState }; database.BotStatuses.Add(row); }
        else Attach(row);
        // A bot that is restarting or signed out reports no groups; keep the last signed-in list so saved selections stay valid.
        if (fresh || input.LoginState == "logged_in" && input.Groups.Length > 0) row.GroupsJson = JsonSerializer.Serialize(input.Groups);
        row.LoginState = input.LoginState; row.LastSummaryAt = input.LastSummaryAt;
        row.LastError = input.LastError; row.ReceivedAt = clock.GetUtcNow();
        row.LoginQrImage = BotChannelJson.QrBytes(input.LoginQrContentType, input.LoginQrBase64); row.LoginQrContentType = input.LoginQrContentType;
        row.LoginQrExpiresAt = row.LoginQrImage is null ? null : row.ReceivedAt.AddMinutes(2); Stamp(row);
    }

    internal async Task AckAsync(CustomerRequestServiceBinding binding, string channel, Guid id, CancellationToken ct)
    {
        var command = await database.BotCommands.SingleOrDefaultAsync(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId && x.Channel == channel && x.Id == id, ct)
            ?? throw new BotChannelNotFoundException();
        command.AcknowledgedAt ??= clock.GetUtcNow();
    }

    public static readonly TimeSpan MessageRetention = TimeSpan.FromDays(90);

    // Only groups an admin selected on Kết nối are kept; anything else the bot sends is dropped.
    internal async Task<BotMessageReceipt> StoreMessagesAsync(CustomerRequestServiceBinding binding, BotMessageBatch input, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var selected = (await GroupsAsync(binding.TenantId, binding.CompanyId, input.Channel, ct)).Select(x => x.ExternalId).ToHashSet(StringComparer.Ordinal);
        var wanted = input.Messages.Where(x => selected.Contains(x.GroupExternalId) && x.SentAt > now - MessageRetention).ToArray();
        var groups = wanted.Select(x => x.GroupExternalId).Distinct().ToArray();
        var ids = wanted.Select(x => x.MessageId).Distinct().ToArray();
        var existing = (await database.BotMessages.AsNoTracking()
            .Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId && x.Channel == input.Channel &&
                groups.Contains(x.GroupExternalId) && ids.Contains(x.MessageId))
            .Select(x => new { x.GroupExternalId, x.MessageId }).ToArrayAsync(ct))
            .Select(x => (x.GroupExternalId, x.MessageId)).ToHashSet();
        var accepted = 0;
        foreach (var message in wanted.Where(x => !existing.Contains((x.GroupExternalId, x.MessageId))))
        {
            database.BotMessages.Add(new()
            {
                TenantId = binding.TenantId,
                CompanyId = binding.CompanyId,
                Channel = input.Channel,
                GroupExternalId = message.GroupExternalId,
                MessageId = message.MessageId,
                SenderName = message.SenderName,
                Text = message.Text ?? "",
                HasImage = message.HasImage,
                SentAt = message.SentAt,
                ReceivedAt = now
            });
            accepted++;
        }
        var cutoff = now - MessageRetention;
        database.BotMessages.RemoveRange(await database.BotMessages
            .Where(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId && x.SentAt < cutoff).Take(500).ToArrayAsync(ct));
        return new(accepted, input.Messages.Length - accepted);
    }

    public async Task<BotMessageGroupView[]> MessageGroupsAsync(AuthorizationContext authority, CancellationToken ct = default)
    {
        await RequireMemberAsync(authority, ct);
        var groups = await database.BotCustomerGroups.AsNoTracking()
            .Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId).ToArrayAsync(ct);
        var stats = await database.BotMessages.AsNoTracking()
            .Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId)
            .GroupBy(x => new { x.Channel, x.GroupExternalId })
            .Select(x => new { x.Key.Channel, x.Key.GroupExternalId, Last = x.Max(m => m.SentAt), Count = x.Count() }).ToArrayAsync(ct);
        await RequireMemberAsync(authority, ct);
        return groups.Select(group =>
        {
            var stat = stats.SingleOrDefault(x => x.Channel == group.Channel && x.GroupExternalId == group.ExternalId);
            return new BotMessageGroupView(group.Channel, group.ExternalId, group.DisplayName, group.CustomerName, stat?.Last, stat?.Count ?? 0);
        }).OrderByDescending(x => x.LastMessageAt ?? DateTimeOffset.MinValue).ThenBy(x => x.DisplayName, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<BotMessagePage> MessagesAsync(AuthorizationContext authority, string channel, string group, DateTimeOffset? before, int limit,
        CancellationToken ct = default)
    {
        BotChannelJson.Channel(channel); CustomerRequestJson.Text(group, 200, opaque: true);
        if (limit is < 1 or > 200) throw new ArgumentException("Invalid bot channel request.");
        await RequireMemberAsync(authority, ct);
        if (!await database.BotCustomerGroups.AsNoTracking().AnyAsync(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId &&
            x.Channel == channel && x.ExternalId == group, ct)) throw new BotChannelNotFoundException();
        var query = database.BotMessages.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId &&
            x.Channel == channel && x.GroupExternalId == group);
        if (before is { } cursor) query = query.Where(x => x.SentAt < cursor);
        var rows = await query.OrderByDescending(x => x.SentAt).ThenByDescending(x => x.MessageId).Take(limit + 1).ToArrayAsync(ct);
        await RequireMemberAsync(authority, ct);
        return new(rows.Take(limit).Select(x => new BotMessageView(x.MessageId, x.SenderName, x.Text, x.HasImage, x.SentAt)).ToArray(), rows.Length > limit);
    }

    private async Task RequireMemberAsync(AuthorizationContext authority, CancellationToken ct)
    {
        if ((await directory.ResolveAsync(authority, ct))?.Context != authority) throw CustomerRequestServiceDirectory.Denied();
    }

    public async Task RequireAdminAsync(AuthorizationContext authority, CancellationToken ct)
    {
        var current = await directory.ResolveAsync(authority, ct);
        if (current?.Context != authority || !current.Roles.Contains("admin", StringComparer.Ordinal)) throw CustomerRequestServiceDirectory.Denied();
    }

    private async Task<BotChannelView> ChannelViewAsync(Guid tenant, Guid company, string channel, CancellationToken ct)
    {
        var row = await ChannelAsync(tenant, company, channel, ct); var status = await StatusAsync(tenant, company, channel, ct);
        return new(channel, row?.Enabled ?? false, row?.DryRun ?? true, row is null ? [] : Deserialize<string[]>(row.SummaryScheduleJson), BotChannelJson.TimeZone,
            row?.TechGroupExternalId, await GroupsAsync(tenant, company, channel, ct), Version(row?.ConfigVersion ?? 0),
            status is null ? null : new(status.LoginState, Deserialize<BotGroup[]>(status.GroupsJson), status.LastSummaryAt, status.LastError, status.ReceivedAt));
    }
    private Task<BotChannelRecord?> ChannelAsync(Guid tenant, Guid company, string channel, CancellationToken ct) => database.BotChannels.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.CompanyId == company && x.Channel == channel, ct);
    private Task<BotStatusRecord?> StatusAsync(Guid tenant, Guid company, string channel, CancellationToken ct) => database.BotStatuses.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.CompanyId == company && x.Channel == channel, ct);
    private Task<BotAiSettingsRecord?> AiAsync(Guid tenant, Guid company, CancellationToken ct) => database.BotAiSettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.CompanyId == company, ct);
    private async Task<BotCustomerGroup[]> GroupsAsync(Guid tenant, Guid company, string channel, CancellationToken ct) =>
        (await database.BotCustomerGroups.AsNoTracking().Where(x => x.TenantId == tenant && x.CompanyId == company && x.Channel == channel).ToArrayAsync(ct))
        .OrderBy(x => x.ExternalId, StringComparer.Ordinal).Select(x => new BotCustomerGroup(x.ExternalId, x.DisplayName, x.CustomerName)).ToArray();
    private async Task<BotChannelRecord> EnsureChannelAsync(Guid tenant, Guid company, string channel, CancellationToken ct)
    {
        var row = await ChannelAsync(tenant, company, channel, ct);
        if (row is not null) return row;
        row = new() { TenantId = tenant, CompanyId = company, Channel = channel, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        Stamp(row); database.BotChannels.Add(row); return row;
    }
    private async Task<DataSourceRegistrationTransaction> BeginCompanyAsync(Guid tenant, Guid company, CancellationToken ct)
    {
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Bot settings are unavailable.");
        var tx = await DataSourceRegistrationTransaction.BeginAsync(database, ct);
        try { await CustomerRequestLocks.AcquireAsync(database, $"company:{tenant:D}:{company:D}", ct); return tx; }
        catch { await tx.DisposeAsync(); throw; }
    }
    private void Attach<T>(T row) where T : class
    {
        var key = database.Model.FindEntityType(typeof(T))!.FindPrimaryKey()!;
        foreach (var entry in database.ChangeTracker.Entries<T>().ToArray())
            if (key.Properties.All(p => Equals(p.PropertyInfo!.GetValue(entry.Entity), p.PropertyInfo.GetValue(row)))) entry.State = EntityState.Detached;
        database.Attach(row);
    }
    private void Stamp(object row)
    {
        if (!database.Database.IsSqlServer()) database.Entry(row).Property("RowVersion").CurrentValue = RandomNumberGenerator.GetBytes(8);
    }
    private void Audit(AuthorizationContext authority, string? channel, string field, string? before, string? after) => database.BotChannelAudits.Add(new()
    { TenantId = authority.TenantId, CompanyId = authority.CompanyId, Id = Guid.NewGuid(), Channel = channel, ActorUserId = authority.UserId, Field = field, Before = before, After = after, Timestamp = clock.GetUtcNow() });
    private static void ClearQr(BotStatusRecord row) { row.LoginQrImage = null; row.LoginQrContentType = null; row.LoginQrExpiresAt = null; }
    private static string Version(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Bot settings are unavailable.");
    private static BotCommandView Command(BotCommandRecord row) => new(row.Id, row.Kind, row.CreatedAt, row.ExpiresAt);
}
