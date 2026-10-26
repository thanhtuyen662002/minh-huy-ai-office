using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class CustomerRequestReplayProtection
{
    // Call only inside the transaction holding the shared service lock. Both bridges share this nonce namespace.
    internal static async Task ConsumeAsync(PlatformDbContext database, CustomerRequestSignature signature, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (await database.CustomerRequestNonces.AnyAsync(x => x.ServiceId == signature.ServiceId && x.Nonce == signature.Nonce, ct))
            throw CustomerRequestServiceDirectory.Denied();
        if (database.Database.IsSqlServer())
            await database.CustomerRequestNonces.Where(x => x.ServiceId == signature.ServiceId && x.ExpiresAt < now).ExecuteDeleteAsync(ct);
        else database.CustomerRequestNonces.RemoveRange(await database.CustomerRequestNonces.Where(x => x.ServiceId == signature.ServiceId && x.ExpiresAt < now).ToArrayAsync(ct));
        database.CustomerRequestNonces.Add(new() { ServiceId = signature.ServiceId, Nonce = signature.Nonce, ExpiresAt = now.AddMinutes(10) });
    }
}
