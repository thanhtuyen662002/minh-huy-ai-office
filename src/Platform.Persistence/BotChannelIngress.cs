using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class BotChannelIngress(PlatformDbContext database, CustomerRequestAuthenticator authentication, BotChannelService service, TimeProvider clock)
{
    public Task<BotConfigView> PullAsync(CustomerRequestSignature signature, string channel, CancellationToken ct = default)
    {
        BotChannelJson.Channel(channel);
        return ExecuteAsync(signature, ReadOnlyMemory<byte>.Empty, "GET", "/internal/bot-config?channel=" + channel,
            binding => service.PullAsync(binding, channel, ct), ct);
    }

    public Task<bool> HeartbeatAsync(CustomerRequestSignature signature, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var captured = body.ToArray();
        var input = BotChannelJson.Heartbeat(BotChannelJson.Read<BotHeartbeat>(captured), clock.GetUtcNow());
        return ExecuteAsync(signature, captured, "POST", "/internal/bot-status", async binding =>
        { await service.HeartbeatAsync(binding, input, ct); return true; }, ct);
    }

    public Task<BotMessageReceipt> MessagesAsync(CustomerRequestSignature signature, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var captured = body.ToArray();
        var input = BotChannelJson.Messages(BotChannelJson.Read<BotMessageBatch>(captured), clock.GetUtcNow());
        return ExecuteAsync(signature, captured, "POST", "/internal/bot-messages", binding => service.StoreMessagesAsync(binding, input, ct), ct);
    }

    public Task<bool> AckAsync(CustomerRequestSignature signature, Guid id, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var captured = body.ToArray(); var input = BotChannelJson.Read<BotCommandAck>(captured); BotChannelJson.Channel(input.Channel);
        if (id == Guid.Empty) throw new ArgumentException();
        return ExecuteAsync(signature, captured, "POST", $"/internal/bot-commands/{id:D}/ack", async binding =>
        { await service.AckAsync(binding, input.Channel, id, ct); return true; }, ct);
    }

    private async Task<T> ExecuteAsync<T>(CustomerRequestSignature signature, ReadOnlyMemory<byte> bytes, string method, string target,
        Func<CustomerRequestServiceBinding, Task<T>> action, CancellationToken ct)
    {
        var binding = await authentication.AuthenticateAsync(signature, bytes, method, target, null, ct);
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Bot bridge is unavailable.");
        await using var tx = await DataSourceRegistrationTransaction.BeginAsync(database, ct);
        await CustomerRequestLocks.AcquireAsync(database, $"service:{signature.ServiceId:D}", ct);
        await CustomerRequestLocks.AcquireAsync(database, $"company:{binding.TenantId:D}:{binding.CompanyId:D}", ct);
        if (!await database.Companies.AsNoTracking().AnyAsync(x => x.TenantId == binding.TenantId && x.Id == binding.CompanyId && x.IsActive, ct))
            throw CustomerRequestServiceDirectory.Denied();
        authentication.RequireFresh(signature.Timestamp);
        await CustomerRequestReplayProtection.ConsumeAsync(database, signature, clock, ct);
        var result = await action(binding);
        authentication.RequireFresh(signature.Timestamp);
        await database.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
    }
}
